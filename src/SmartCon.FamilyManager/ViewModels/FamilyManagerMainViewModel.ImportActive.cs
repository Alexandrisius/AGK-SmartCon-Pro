using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.Import;
using SmartCon.UI;
using SmartCon.UI.Behaviors;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task ImportActiveFileAsync()
    {
        using var _scope = SmartConLogger.BeginScope("FMImport",
            ("Method", "ImportActiveFileAsync"));
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;
        IsLoading = true;
        var sessionStart = DateTime.Now;
        try
        {
            SmartConLogger.LogSessionStart("ImportActiveFile");

            var kind = await _activeDocumentClassifier.ClassifyAsync();
            SmartConLogger.Info($"Active document kind: {kind}");

            switch (kind)
            {
                case ActiveDocumentKind.None:
                    _dialogService.ShowError(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportErrorTitle) ?? "Error",
                        LanguageManager.GetString(StringLocalization.Keys.FM_ActiveDocNotProject)
                            ?? "Активный документ не является проектом. Откройте проект Revit.");
                    return;

                case ActiveDocumentKind.Family:
                    await ProcessFamilyImportAsync();
                    break;

                case ActiveDocumentKind.Project:
                    string? capturedActivePath = null;
                    var isMiniProjectDoc = false;
                    var (systemAnalysesRaw, loadableFamilies) = await _awaitableEvent.RaiseAsync<(IReadOnlyList<CategoryAnalysis>, IReadOnlyList<LoadableFamilyInfo>)>(obj =>
                    {
                        try
                        {
                            var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                            var activeDoc = uiApp.ActiveUIDocument?.Document;
                            if (activeDoc is null) return (Array.Empty<CategoryAnalysis>(), Array.Empty<LoadableFamilyInfo>());

                            capturedActivePath = activeDoc.PathName;
                            // Stress test 2026-08-05: in a mini-project the
                            // pre-import confirmation is pure noise — one
                            // category, a couple of types, nothing "long" to
                            // warn about. Detected here (Revit thread) because
                            // the ES marker read needs the API context.
                            isMiniProjectDoc = _miniProjectMarker.IsMiniProject(activeDoc)
                                || MiniProjectPathPattern.IsMiniProjectPath(activeDoc.PathName);
                            var sys = _systemFamilyRevitOps.AnalyzeActiveProject(activeDoc);
                            var load = _loadableFamilyScanner.GetUniqueFamilies(activeDoc);
                            return (sys, load);
                        }
                        catch (Exception ex)
                        {
                            SmartConLogger.Error(
                                $"Analyze failed: {ex.Message}");
                            return (Array.Empty<CategoryAnalysis>(), Array.Empty<LoadableFamilyInfo>());
                        }
                    });

                    // ADR-027 Phase 2: categories whose placement API is missing
                    // on this Revit version (ceilings <2022, railings <2025) are
                    // excluded with a styled info dialog — a staged mini-project
                    // without placed instances is not a valid reference.
                    var systemAnalyses = ApplyPlacementVersionGate(systemAnalysesRaw);

                    var systemTypeCount = systemAnalyses.Sum(a => a.TypeCount);
                    var systemCategoryCount = systemAnalyses.Count;
                    var loadableCount = loadableFamilies.Count;

                    SmartConLogger.Info(
                        $"Phase 1 (fast): system={systemCategoryCount}cat/{systemTypeCount}types, loadable={loadableCount} families");

                    if (systemCategoryCount == 0 && loadableCount == 0)
                    {
                        _dialogService.ShowError(
                            LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "Error",
                            LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound)
                                ?? "В проекте не найдено размещённых семейств для импорта");
                        return;
                    }

                    if (isMiniProjectDoc)
                    {
                        // Mini-project reimport (curator workflow): skip the
                        // confirmation — its purpose is to warn about a long
                        // import of a big REAL project.
                        SmartConLogger.Info("Phase 2: mini-project — confirmation skipped");
                    }
                    else
                    {
                        var confirmMessage = string.Format(
                            LanguageManager.GetString(StringLocalization.Keys.FM_ImportActiveConfirmMessage)
                                ?? "Импортировать в каталог: {0} системных категорий ({1} типов) и {2} загружаемых семейств?",
                            systemCategoryCount, systemTypeCount, loadableCount);
                        var confirmed = _dialogService.ShowConfirmation(
                            LanguageManager.GetString(StringLocalization.Keys.FM_ImportActiveConfirmTitle)
                                ?? "Импорт активного файла",
                            confirmMessage);
                        SmartConLogger.Info($"Phase 2: user confirmed={confirmed}");
                        if (!confirmed)
                        {
                            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Cancel) ?? "Отменено";
                            return;
                        }
                    }

                    var batchItems = await BuildActiveProjectBatchItemsAsync(systemAnalyses, loadableFamilies);
                    if (batchItems.Count == 0)
                    {
                        _dialogService.ShowError(
                            LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Error",
                            LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "Не удалось подготовить семейства для импорта");
                        return;
                    }
                    var outcome = await ProcessProjectImportAsync(batchItems);
                    // #186: after a successful reimport close the reference
                    // mini-project and return focus to the work project —
                    // consistent with the .rfa flow (CloseFamilyDocumentAsync).
                    // A document without the mini-project ES marker (#188) is
                    // NEVER closed — that is the user's real work project.
                    if (outcome.ImportStarted && outcome.SuccessCount > 0
                        && !string.IsNullOrEmpty(capturedActivePath))
                    {
                        await CloseMiniProjectAfterImportAsync(capturedActivePath!);
                    }

                    // #185: automatic stale check right after the import —
                    // the work project (now active) holds markers of the
                    // previous catalog version; the user must SEE that
                    // "Обновить" is needed, without hunting for "Проверить".
                    if (outcome.ImportStarted && outcome.SuccessCount > 0)
                    {
                        await RunPostImportStaleCheckAsync(outcome.ImportedItems);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FAILED: {ex}");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportErrorTitle) ?? "Error",
                ex.Message);
        }
        finally
        {
            // v2.0.0: no temp staging, no document close. The active family
            // document remains open across the import — SaveAs into managed
            // storage does NOT close it. User sees the family file with its
            // PathName now pointing to the managed copy, but the document is
            // still editable in-place until they choose to close it.
            IsLoading = false;
            SmartConLogger.LogSessionEnd("ImportActiveFile", sessionStart);
        }
    }

    /// <summary>
    /// E2 (#209): imports the shared-nested children of the active family
    /// through the regular file-batch executor — staging resolves the
    /// held-open <c>nested://</c> documents exactly like UC-1. The parent
    /// itself was imported through the bespoke active-document path, so its
    /// catalog id arrives via <paramref name="externalParentItemIds"/> and
    /// the executor's link write lands on the parent's current version.
    /// </summary>
    private async Task<FamilyBatchImportExecutionResult> ImportActiveFamilyChildrenAsync(
        IReadOnlyList<FamilyBatchImportItem> childImports,
        IReadOnlyDictionary<string, string>? externalParentItemIds)
    {
        var staging = new FileFamilyStagingService(
            _awaitableEvent, _preparationService, _importService);
        var executor = new FileFamilyBatchImportExecutor(
            staging,
            _importService,
            _familyDependencyRepository,
            _dataImportService,
            _sharedNestedRepository,
            CurrentRevitVersion,
            _routingRuleRepository,
            _segmentSizeRepository,
            _segmentRuleRepository);
        var result = await executor.ExecuteAsync(
                childImports, categoryId: null, progress: null, pauseGate: null,
                CancellationToken.None, externalParentItemIds)
            .ConfigureAwait(false);
        SmartConLogger.Info(
            $"Active-family nested import: success={result.SuccessCount}, " +
            $"skipped={result.SkippedCount}, errors={result.ErrorCount}");
        return result;
    }

    /// <summary>Snapshot of an active family document captured on the Revit UI thread.</summary>
    /// <remarks>
    /// v2.0.0: <c>HasTypeCatalog</c> removed. ADR-033 bakes the Type Catalog
    /// into the managed .rfa at import time, so the active document no
    /// longer needs to advertise whether a sidecar exists.
    /// </remarks>
    private sealed record ActiveFamilySnapshot(
        Document? Document,
        string BaseName,
        int RevitVersion,
        string? OriginalPathName);

    private async Task<ProjectImportOutcome> ProcessProjectImportAsync(List<FamilyBatchImportItem> batchItems)
    {
        using var _ = SmartConLogger.BeginScope("FMImport",
            ("Method", "ProcessProjectImportAsync"));
        if (batchItems.Count == 0)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "No families found");
            return ProjectImportOutcome.NotStarted;
        }

        // ProcessProjectImportAsync is invoked from:
        //   1) "Импорт активного файла" (case Project)
        //   2) "Импорт выделенных элементов"
        // Neither path carries a user-selected category intent — the user did
        // not click "Импорт в категорию". The default behaviour in this flow
        // matches the legacy ImportFiles command: every row opens with
        // "Без категории" and the user can pick a target per row, or leave
        // it empty to keep the family un-categorised. The "Импорт в категорию"
        // command is a SEPARATE entry point (ImportFileToCategoryAsync) that
        // resolves defaultCategoryId from SelectedTreeNode — see
        // FamilyManagerMainViewModel.Import.cs:ImportFileToCategoryAsync.
        if (_batchDialogOpen)
        {
            SmartConLogger.Warn(
                "Batch import dialog is already open — ignoring re-entry " +
                "[Action: дождитесь завершения текущего импорта или закройте его диалог]");
            return ProjectImportOutcome.NotStarted;
        }
        _batchDialogOpen = true;

        try
        {
        var staging = new ProjectFamilyStagingService(
            _awaitableEvent,
            _preparationService,
            _revitContext,
            _systemFamilyIsolationProject,
            _importService,
            _databaseManager);
        var executor = new ProjectFamilyBatchImportExecutor(
            staging,
            _systemFamilyImportOrchestrator,
            _systemFamilyAttributeExtractor,
            _loadableFamilyImportOrchestrator,
            _dataImportService,
            _sharedNestedRepository,
            _versionWriter,
            _staleDetector,
            _catalogProvider,
            _familyDependencyRepository,
            CurrentRevitVersion,
            _routingRuleRepository,
            _segmentSizeRepository,
            _segmentRuleRepository);

        using var vm = new FamilyBatchImportViewModel(
            batchItems,
            _dialogService,
            _viewModelFactory,
            defaultCategoryId: null,
            defaultCategoryName: null,
            _catalogProvider,
            importPrecomputer: _importPrecomputer,
            dedupService: _dedupService,
            executor: executor,
            publishedByUser: _revitContext.GetUsername(),
            dispatcher: _dispatcher,
            validationService: _validationService,
            autoAssignService: _autoAssignService,
            analyticsRepository: _contentHashAnalytics);

        _dialogService.ShowModelessBatchImportDialog(vm);
        await vm.DialogCompletion;

        if (!vm.ImportStarted)
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            return ProjectImportOutcome.NotStarted;
        }

        if (vm.ImportSuccessCount > 0)
        {
            await LoadTreeAsync();
        }

        StatusMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_SummaryFormat)
                ?? "Импортировано: {0}, пропущено: {1}, ошибок: {2}",
            vm.ImportSuccessCount, vm.ImportSkippedCount, vm.ImportErrorCount);

        // #185/#186: collect the catalog items this run touched so callers can
        // run post-import actions (safe mini-project close, stale check).
        // Owner stress test 2026-09-01 (баг 4): read the FINAL row state, not
        // the stale DTOs — a Duplicate row defaults to Action=Skip on the DTO,
        // so the user's «Новая версия» (row-level IncrementVersion) was
        // filtered out here and the post-import stale check silently never
        // ran (the orange VersionMismatch badge appeared only on a manual
        // properties open).
        var importedItems = vm.Items
            .Where(r => r.Action != FamilyBatchImportAction.Skip)
            .Select(r => new { Id = r.PrecomputedCatalogItemId ?? r.ExistingCatalogItemId, Row = r })
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .Select(x => new ImportedCatalogItem(x.Id!, x.Row.FileName, x.Row.FamilySource))
            .ToList();
        return new ProjectImportOutcome(true, vm.ImportSuccessCount, importedItems);
        }
        finally
        {
            _batchDialogOpen = false;
        }
    }

    private async Task ExtractAttributesForLoadableTasks(IReadOnlyList<LoadableFamilyAttributeTask> tasks)
    {
        using var _scope = SmartConLogger.BeginScope("FMLoadable",
            ("Method", "ExtractAttributesForLoadableTasks"),
            ("Count", tasks.Count));
        var helper = new LoadableAttributeExtractionHelper(
            _dataImportService, _sharedNestedRepository, CurrentRevitVersion);
        foreach (var task in tasks)
        {
            await helper.ExtractAsync(task, CancellationToken.None);
        }
    }
}
