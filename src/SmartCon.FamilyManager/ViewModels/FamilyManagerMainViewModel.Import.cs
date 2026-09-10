using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.Import;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private async Task ImportFilesAsync()
    {
        using var _scope = SmartConLogger.BeginScope("FMImport",
            ("Method", "ImportFilesAsync"));
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFile) ?? "Import Files";
        var paths = _dialogService.ShowImportFilesDialog(title);
        if (paths is null || paths.Length == 0) return;

        await ShowBatchImportDialogAsync(paths, null);
    }

    [RelayCommand(CanExecute = nameof(CanImportToCategoryWithAccess))]
    private async Task ImportFileToCategoryAsync()
    {
        if (SelectedTreeNode is not CategoryNodeViewModel categoryNode) return;
        if (categoryNode.CategoryId == "__no_category__") return;
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFile) ?? "Import File";
        var paths = _dialogService.ShowImportFilesDialog(title);
        if (paths is null || paths.Length == 0) return;

        await ShowBatchImportDialogAsync(paths, categoryNode.CategoryId);
    }

    private bool _batchDialogOpen;

    /// <summary>
    /// Shows the batch import dialog for the given file paths.
    /// Phase 27: uses FamilyImportPreparationService for unified
    /// open → extract → hash → dedup in a single pass. Documents are
    /// held open until the dialog is confirmed or cancelled.
    /// </summary>
    private async Task ShowBatchImportDialogAsync(string[] paths, string? categoryId, string? forcedExistingItemId = null)
    {
        using var _scope = SmartConLogger.BeginScope("BatchImport",
            ("Method", nameof(ShowBatchImportDialogAsync)),
            ("Paths", paths.Length),
            ("CategoryId", categoryId ?? "<none>"));

        if (_batchDialogOpen)
        {
            SmartConLogger.Warn(
                "Batch import dialog is already open — ignoring re-entry " +
                "[Action: дождитесь завершения текущего импорта или закройте его диалог]");
            return;
        }
        _batchDialogOpen = true;

        IsLoading = true;
        try
        {
            string? categoryName = null;
            if (!string.IsNullOrEmpty(categoryId))
            {
                try
                {
                    var cat = await _categoryRepository.GetByIdAsync(categoryId!, CancellationToken.None);
                    categoryName = cat?.Name;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Failed to resolve category name for '{categoryId}': {ex.Message} [Action: проверьте, что категория существует в каталоге и БД доступна]");
                }
            }

            SmartConLogger.Info($"Preparing {paths.Length} file(s)...");
            var preparedItems = await _preparationService.PrepareForFileImportAsync(paths, CancellationToken.None);
            SmartConLogger.Info($"Prepared {preparedItems.Count} items");
            var items = new List<FamilyBatchImportItem>(preparedItems.Count);

            foreach (var p in preparedItems)
            {
                string? existingCategoryId = null;
                string? existingCategoryName = null;

                if (p.ExistingCatalogItemId is not null)
                {
                    try
                    {
                        var existingItem = await _catalogProvider.GetItemAsync(p.ExistingCatalogItemId, CancellationToken.None);
                        existingCategoryId = existingItem?.CategoryId;
                        existingCategoryName = existingItem?.CategoryPath;
                        if (existingCategoryId is not null && existingCategoryName is null)
                        {
                            var cat = await _categoryRepository.GetByIdAsync(existingCategoryId, CancellationToken.None);
                            existingCategoryName = cat?.Name;
                        }
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Warn($"Failed to resolve category for existing item '{p.ExistingCatalogItemId}': {ex.Message} [Action: проверьте, что БД доступна]");
                    }
                }

                var status = p.ErrorMessage is not null
                    ? FamilyBatchImportStatus.Error
                    : p.Status;

                var precomputed = await _importPrecomputer
                    .BuildPrecomputedTripleAsync(p.DisplayName, ".rfa", p.FamilySource, p.ExistingCatalogItemId, CancellationToken.None)
                    .ConfigureAwait(false);

                items.Add(new FamilyBatchImportItem(
                    FilePath: p.SourcePath,
                    FileName: p.DisplayName,
                    RevitMajorVersion: p.RevitMajorVersion,
                    Status: status,
                    ExistingCatalogItemId: forcedExistingItemId ?? p.ExistingCatalogItemId,
                    ExistingVersionLabel: p.ExistingVersionLabel,
                    TargetCategoryId: categoryId ?? existingCategoryId,
                    TargetCategoryName: categoryName ?? existingCategoryName,
                    FamilySource: p.FamilySource,
                    TypeCount: SnapshotExtractionMapper.ResolveTypeCount(
                        p.LoadableSnapshot, p.SystemSnapshot, p.SourceTypes),
                    RevitCategory: p.LoadableSnapshot?.Category,
                    OriginalSourcePath: null,
                    SourceTypes: p.SourceTypes,
                    Source: p.Source,
                    PrecomputedCatalogItemId: precomputed?.CatalogItemId,
                    PrecomputedVersionLabel: precomputed?.VersionLabel,
                    PrecomputedManagedPath: precomputed?.ManagedPath,
                    ContentHash: p.ContentHash?.HexString,
                    HashFormatVersion: p.ContentHash?.FormatVersion,
                    MatchedVersionLabel: p.MatchedVersionLabel,
                    LoadableSnapshot: p.LoadableSnapshot,
                    SystemSnapshot: p.SystemSnapshot,
                    GeometryPerType: p.GeometryPerType,
                    IsCrossNameDuplicate: p.IsCrossNameDuplicate,
                    MatchedItemName: p.MatchedItemName,
                    ExistingCategoryId: existingCategoryId,
                    ExistingCategoryPath: existingCategoryName,
                    HealthReport: p.HealthReport,
                    DependencyLinks: p.DependencyLinks,
                    IsMarkerResolvedVersion: p.IsMarkerResolvedVersion,
                    PerTypeHashes: p.PerTypeHashes,
                    Sections: p.Sections,
                    UnsubstitutedMiniRouting: p.UnsubstitutedMiniRouting)
                {
                    // ADR-066: dependency rows exist to guarantee PRESENCE in
                    // the catalog. Duplicates default to Skip (dedup-link).
                    // E2 (#209, owner decision): a shared-nested row with NEW
                    // content of an existing item imports like a regular
                    // family — new ACTIVE version (other parents embedding
                    // the older copy get the drift badge); routing rows stay
                    // conservative (Skip) — system parents resolve children
                    // dynamically at their active version anyway.
                    Action = status == FamilyBatchImportStatus.Duplicate
                        ? FamilyBatchImportAction.Skip
                        : p.DependencyLinks is not null && status == FamilyBatchImportStatus.Existing
                            ? (p.DependencyLinks.Any(l => l.Kind == FamilyDependencyKind.SharedNested)
                                ? FamilyBatchImportAction.IncrementVersion
                                : FamilyBatchImportAction.Skip)
                            : FamilyBatchImportAction.IncrementVersion
                });
            }

            SmartConLogger.Info($"Built {items.Count} batch items, creating ViewModel...");
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
            using var vm = new FamilyBatchImportViewModel(
                items,
                _dialogService,
                _viewModelFactory,
                categoryId,
                categoryName,
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
                return;
            }

            StatusMessage = BuildImportStatusMessage(
                vm.ImportSuccessCount, vm.ImportSkippedCount, vm.ImportErrorCount, items.Count);

            if (vm.ImportSuccessCount > 0)
            {
                await LoadTreeAsync();
            }

            var completedMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportCompleted) ?? "Import {0}/{1} families completed",
                vm.ImportSuccessCount, items.Count);
            _freezeRecovery.Nudge(completedMessage);

            // Auto-clear status after 10 seconds
            FireAndForget(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                SmartConLogger.Debug(
                    $"Auto-clearing StatusMessage (thread={Environment.CurrentManagedThreadId}, marshalling via _dispatcher)");
                _ = _dispatcher.InvokeAsync(() => StatusMessage = string.Empty);
            }, nameof(ImportFilesAsync));
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"ImportFilesAsync failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace} " +
                "[Action: check smartcon.log for the failing family; prepared documents will be closed]");
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
            _batchDialogOpen = false;
        }
    }

    private bool CanImportToCategory() =>
        SelectedTreeNode is CategoryNodeViewModel cat && cat.CategoryId != "__no_category__";

    private string BuildImportStatusMessage(int successCount, int skipCount, int errorCount, int total)
    {
        var parts = new List<string>();

        var importPart = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_ImportStatusImport) ?? "импорт: {0}/{1}",
            successCount, total);
        parts.Add(importPart);

        if (skipCount > 0)
        {
            var skipPart = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportStatusSkipped) ?? "пропущено: {0}",
                skipCount);
            skipPart += LanguageManager.GetString(StringLocalization.Keys.FM_ImportStatusSkippedIdentical) ?? " (идентично)";
            parts.Add(skipPart);
        }

        if (errorCount > 0)
        {
            var errorPart = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportStatusErrors) ?? "ошибок: {0}",
                errorCount);
            parts.Add(errorPart);
        }

        return string.Join(", ", parts);
    }

    private bool CanImportFiles() => CanImport;

    private bool CanImportToCategoryWithAccess() => CanImport && CanImportToCategory();

}
