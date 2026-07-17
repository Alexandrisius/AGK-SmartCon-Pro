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
                    .BuildPrecomputedTripleAsync(p.DisplayName, ".rfa", p.ExistingCatalogItemId, CancellationToken.None)
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
                    RevitCategory: null,
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
                    MatchedItemName: p.MatchedItemName)
                {
                    Action = status == FamilyBatchImportStatus.Duplicate
                        ? FamilyBatchImportAction.Skip
                         : FamilyBatchImportAction.IncrementVersion
                });
            }

            SmartConLogger.Info($"Built {items.Count} batch items, creating ViewModel...");
            var staging = new FileFamilyStagingService(
                _awaitableEvent, _preparationService, _importService);
            var executor = new FileFamilyBatchImportExecutor(
                staging,
                _importService,
                _dataImportService,
                _sharedNestedRepository,
                CurrentRevitVersion);
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
                publishedByUser: _revitContext.GetUsername());

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

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task ImportSelectedElementsAsync()
    {
        IsLoading = true;
        StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SelectElementsPrompt)
            ?? "Выберите элементы в Revit (системные или загружаемые семейства)...";

        SelectedElementsAnalysis? analysis = null;

        await _awaitableEvent.RaiseAsync(_ =>
        {
            try
            {
                var doc = _revitContext.GetDocument();
                if (doc is null || doc.IsFamilyDocument)
                {
                    _dialogService.ShowWarning(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportSelectedElements) ?? "Импорт выделенных элементов",
                        LanguageManager.GetString(StringLocalization.Keys.FM_ActiveDocNotProject)
                            ?? "Активный документ не является проектом. Откройте проект Revit.");
                    IsLoading = false;
                    return;
                }

                analysis = _systemFamilyRevitOps.PickSelectedElements();
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Ошибка импорта: {0}",
                    ex.Message);
                IsLoading = false;
            }
        });

        if (analysis is null || analysis.IsEmpty)
        {
            if (analysis is not null)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImportFailed)
                    ?? "Не удалось подготовить элементы";
                IsLoading = false;
            }
            return;
        }

        try
        {
            var batchItems = await BuildSelectedElementsBatchItemsAsync(analysis);

            if (batchItems.Count == 0)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImportFailed)
                    ?? "Не удалось подготовить элементы";
                IsLoading = false;
                return;
            }

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyPreparing)
                    ?? "Импорт {0} элементов...",
                batchItems.Count);

            await ProcessProjectImportAsync(batchItems);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"failed: {ex.Message}");
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Ошибка импорта: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Returns display name for the single-category case: the .rvt file
    /// name of the active project, without extension. Falls back to
    /// <see cref="Autodesk.Revit.DB.Document.Title"/> for unsaved projects.
    /// </summary>
    private static string ResolveActiveProjectDisplayName(Autodesk.Revit.DB.Document activeDoc)
    {
        var pathName = activeDoc.PathName;
        if (!string.IsNullOrEmpty(pathName))
        {
            var name = SafeFileName.GetBaseName(pathName);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return activeDoc.Title;
    }

    private async Task<List<FamilyBatchImportItem>> BuildSelectedElementsBatchItemsAsync(
        SelectedElementsAnalysis analysis)
    {
        var ct = CancellationToken.None;

        var systemAnalyses = analysis.SystemTypes
            .GroupBy(s => s.Category)
            .Select(g => new CategoryAnalysis(
                g.Key,
                g.First().CategoryName,
                g.Select(t => new SystemTypeInfo(t.Name, t.UniqueId)).ToList()))
            .ToList();

        var prepared = await _preparationService.PrepareProjectImportAsync(
            systemAnalyses, analysis.LoadableFamilies, ct);

        return await MapPreparedItemsToBatchItemsAsync(prepared, ct);
    }

    private async Task<List<FamilyBatchImportItem>> BuildActiveProjectBatchItemsAsync(
        IReadOnlyList<CategoryAnalysis> systemAnalyses,
        IReadOnlyList<LoadableFamilyInfo> loadableFamilies)
    {
        var ct = CancellationToken.None;

        string? projectNameOverride = null;
        if (systemAnalyses.Count == 1 && loadableFamilies.Count == 0)
        {
            var activeDoc = _revitContext.GetDocument();
            if (activeDoc is not null)
            {
                projectNameOverride = ResolveActiveProjectDisplayName(activeDoc);
                SmartConLogger.Info(
                    $"[FMImport] Single system category with no loadable families — " +
                    $"using source file name '{projectNameOverride}' as displayName");
            }
        }

        var prepared = await _preparationService.PrepareProjectImportAsync(
            systemAnalyses, loadableFamilies, ct);

        if (projectNameOverride is not null && prepared.Count == 1 && prepared[0].FamilySource == "system")
        {
            prepared = new List<PreparedFamilyItem>
            {
                prepared[0] with
                {
                    DisplayName = projectNameOverride,
                    SourcePath = $"system://{SafeFileName.GetBaseName(projectNameOverride)}"
                }
            };
        }

        return await MapPreparedItemsToBatchItemsAsync(prepared, ct);
    }

    /// <summary>
    /// Maps PreparedFamilyItem list to FamilyBatchImportItem list.
    /// Does precompute triple + category resolution for each item.
    /// </summary>
    private async Task<List<FamilyBatchImportItem>> MapPreparedItemsToBatchItemsAsync(
        IReadOnlyList<PreparedFamilyItem> prepared, CancellationToken ct)
    {
        var allCategories = await _categoryRepository.GetAllAsync(ct).ConfigureAwait(false);
        var categoriesById = allCategories.ToDictionary(c => c.Id);

        var result = new List<FamilyBatchImportItem>(prepared.Count);

        foreach (var p in prepared)
        {
            string? existingCategoryId = null;
            string? existingCategoryName = null;

            if (p.ExistingCatalogItemId is not null)
            {
                try
                {
                    var existingItem = await _catalogProvider.GetItemAsync(p.ExistingCatalogItemId, ct);
                    existingCategoryId = existingItem?.CategoryId;
                    existingCategoryName = existingItem?.CategoryPath;
                    if (existingCategoryId is not null && existingCategoryName is null && categoriesById.TryGetValue(existingCategoryId, out var cat))
                        existingCategoryName = cat.FullPath ?? cat.Name;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Failed to resolve category for existing item '{p.ExistingCatalogItemId}': {ex.Message} [Action: проверьте, что БД доступна]");
                }
            }

            var extension = p.FamilySource == "system" ? ".rvt" : ".rfa";
            // Issue #126: when dedup matched this row to an existing item
            // by content hash (possibly under a different name), the
            // precomputed triple must target THAT item, not a name lookup.
            var precomputed = await _importPrecomputer
                .BuildPrecomputedTripleAsync(p.DisplayName, extension, p.ExistingCatalogItemId, ct)
                .ConfigureAwait(false);

            var status = p.ErrorMessage is not null
                ? FamilyBatchImportStatus.Error
                : p.Status;

            result.Add(new FamilyBatchImportItem(
                FilePath: p.SourcePath,
                FileName: p.DisplayName,
                RevitMajorVersion: p.RevitMajorVersion,
                Status: status,
                ExistingCatalogItemId: p.ExistingCatalogItemId,
                ExistingVersionLabel: p.ExistingVersionLabel,
                TargetCategoryId: existingCategoryId,
                TargetCategoryName: existingCategoryName,
                FamilySource: p.FamilySource,
                TypeCount: SnapshotExtractionMapper.ResolveTypeCount(
                    p.LoadableSnapshot, p.SystemSnapshot, p.SourceTypes),
                RevitCategory: null,
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
                IsCrossNameDuplicate: p.IsCrossNameDuplicate,
                MatchedItemName: p.MatchedItemName)
            {
                Action = status == FamilyBatchImportStatus.Duplicate
                    ? FamilyBatchImportAction.Skip
                    : FamilyBatchImportAction.IncrementVersion
            });
        }

        return result;
    }

    private async Task<FamilyBatchImportItem?> BuildSystemFamilyBatchRowVirtualAsync(
        string displayName,
        IReadOnlyList<FamilySourceTypeInfo> coreTypes,
        FamilyImportSource source,
        string placeholderPath,
        CancellationToken ct,
        IReadOnlyDictionary<string, CategoryNode>? categoriesById = null)
    {
        // v2.0.0: virtual batch row. No managed file is on disk yet, so
        // we cannot read its Revit version — the orchestrator writes the
        // .rvt AFTER the dialog confirms, then determines the version.
        // We use CurrentRevitVersion as a reasonable default for the
        // preview column (the user sees the version of Revit they're
        // running, which matches the version of the .rvt we will write).
        var revitVersion = CurrentRevitVersion;

        var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(displayName);
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, ct).ConfigureAwait(false);

        FamilyBatchImportStatus status;
        string? existingId = null;
        string? existingVersionLabel = null;
        string? targetCategoryId = null;
        string? targetCategoryName = null;

        if (existingByName is not null)
        {
            status = FamilyBatchImportStatus.Existing;
            existingId = existingByName.Id;
            existingVersionLabel = existingByName.CurrentVersionLabel;
            targetCategoryId = existingByName.CategoryId;
            targetCategoryName = existingByName.CategoryPath;
        }
        else
        {
            status = FamilyBatchImportStatus.New;
        }

        if (targetCategoryId is not null && categoriesById is not null
            && categoriesById.TryGetValue(targetCategoryId, out var cat) && cat is not null)
        {
            targetCategoryName = cat.FullPath ?? cat.Name;
        }

        // v2.0.0: precompute the canonical (catalogItemId, versionLabel,
        // managedRfaPath) triple through the precomputer — the SAME
        // service the dialog rename handler uses, so the initial build
        // and the post-rename re-derivation agree on every value (id +
        // version + path all move together, never piecemeal). The
        // precomputer itself re-runs FindByNormalizedNameAsync, so we
        // pay one extra read here; the trade-off is worth it for the
        // invariant guarantee.
        var precomputed = await _importPrecomputer
            .BuildPrecomputedTripleAsync(displayName, ".rvt", null, ct)
            .ConfigureAwait(false);

        SmartConLogger.Info(
            $"[FMImport.BuildSystem] displayName='{displayName}', " +
            $"existingByName={(existingByName?.Id ?? "<null>")}, " +
            $"precomputedCatalogItemId='{precomputed?.CatalogItemId ?? "<null>"}', " +
            $"precomputedVersionLabel='{precomputed?.VersionLabel ?? "<null>"}', " +
            $"precomputedManagedPath='{precomputed?.ManagedPath ?? "<null>"}'");

        return new FamilyBatchImportItem(
            FilePath: placeholderPath,
            FileName: displayName,
            RevitMajorVersion: revitVersion,
            Status: status,
            ExistingCatalogItemId: existingId,
            ExistingVersionLabel: existingVersionLabel,
            TargetCategoryId: targetCategoryId,
            TargetCategoryName: targetCategoryName,
            FamilySource: "system",
            TypeCount: coreTypes.Count,
            RevitCategory: displayName,
            SourceTypes: coreTypes,
            Source: source,
            PrecomputedCatalogItemId: precomputed?.CatalogItemId,
            PrecomputedVersionLabel: precomputed?.VersionLabel,
            PrecomputedManagedPath: precomputed?.ManagedPath);
    }

    private async Task<FamilyBatchImportItem?> BuildLoadableFamilyBatchRowVirtualAsync(
        LoadableFamilyInfo loadable,
        FamilyImportSource source,
        string placeholderPath,
        CancellationToken ct,
        IReadOnlyDictionary<string, CategoryNode>? categoriesById = null)
    {
        // v2.0.0: virtual batch row. No managed .rfa is on disk yet, so
        // the orchestrator's post-dialog flow will allocate the path,
        // call EditFamily + SaveAs into managed storage, and determine
        // the actual Revit version from the resulting file. For the
        // preview column we use the running Revit's version, which
        // matches the version of the .rfa we will produce.
        var revitVersion = CurrentRevitVersion;

        var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(loadable.FamilyName);
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, ct).ConfigureAwait(false);

        SmartConLogger.Debug(
            $"file='{loadable.FamilyName}' normalized='{normalizedName}' " +
            $"byName={(existingByName is null ? "null" : $"Id={existingByName.Id} CatId={existingByName.CategoryId ?? "<null>"} CatPath={existingByName.CategoryPath ?? "<null>"}")}");

        FamilyBatchImportStatus status;
        string? existingId = null;
        string? existingVersionLabel = null;
        string? targetCategoryId = null;
        string? targetCategoryName = null;

        if (existingByName is not null)
        {
            status = FamilyBatchImportStatus.Existing;
            existingId = existingByName.Id;
            existingVersionLabel = existingByName.CurrentVersionLabel;
            targetCategoryId = existingByName.CategoryId;
            targetCategoryName = existingByName.CategoryPath;
        }
        else
        {
            status = FamilyBatchImportStatus.New;
        }

        if (targetCategoryId is not null && categoriesById is not null
            && categoriesById.TryGetValue(targetCategoryId, out var cat) && cat is not null)
        {
            targetCategoryName = cat.FullPath ?? cat.Name;
        }

        // v2.0.0: same rationale as BuildSystemFamilyBatchRowVirtualAsync
        // — go through the precomputer so the initial build and the
        // post-rename re-derivation cannot drift apart.
        var precomputed = await _importPrecomputer
            .BuildPrecomputedTripleAsync(loadable.FamilyName, ".rfa", null, ct)
            .ConfigureAwait(false);

        return new FamilyBatchImportItem(
            FilePath: placeholderPath,
            FileName: loadable.FamilyName,
            RevitMajorVersion: revitVersion,
            Status: status,
            ExistingCatalogItemId: existingId,
            ExistingVersionLabel: existingVersionLabel,
            TargetCategoryId: targetCategoryId,
            TargetCategoryName: targetCategoryName,
            FamilySource: "loadable",
            TypeCount: loadable.TypeCount,
            RevitCategory: loadable.CategoryName,
            OriginalSourcePath: null,
            SourceTypes: null,
            Source: source,
            PrecomputedCatalogItemId: precomputed?.CatalogItemId,
            PrecomputedVersionLabel: precomputed?.VersionLabel,
            PrecomputedManagedPath: precomputed?.ManagedPath);
    }
}
