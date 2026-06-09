using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
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

    /// <summary>
    /// Shows the batch import dialog for the given file paths.
    /// Collects SHA256, Revit version, deduplicates, then runs ImportBatchAsync.
    /// </summary>
    private async Task ShowBatchImportDialogAsync(string[] paths, string? categoryId, string? forcedExistingItemId = null)
    {
        IsLoading = true;
        try
        {
            var items = new List<FamilyBatchImportItem>();
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
                    SmartConLogger.Warn($"Failed to resolve category name for '{categoryId}': {ex.Message}");
                }
            }

            foreach (var path in paths)
            {
                try
                {
                    var sha256 = await _metadataService.ExtractAsync(path, CancellationToken.None);
                    var revitVersion = _fileInfoReader.ReadRevitVersion(path) ?? CurrentRevitVersion;
                    var fileInfo = new FileInfo(path);

                    // Deduplication: check SHA256 across all families
                    var existingByHash = await _catalogProvider.FindByHashAsync(sha256.Sha256, CancellationToken.None);
                    if (existingByHash is not null)
                    {
                        items.Add(new FamilyBatchImportItem(
                            path, SafeFileName.GetBaseName(path), sha256.Sha256, revitVersion, fileInfo.Length,
                            FamilyBatchImportStatus.Duplicate,
                            existingByHash.CatalogItemId, existingByHash.VersionLabel,
                            categoryId, categoryName));
                        continue;
                    }

                    // Check by normalized name
                    var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(SafeFileName.GetBaseName(path));
                    var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None);
                    if (existingByName is not null || forcedExistingItemId is not null)
                    {
                        var existingCategoryId = existingByName?.CategoryId;
                        var existingCategoryName = existingByName?.CategoryPath;
                        // category_name in DB can be NULL while category_id is set — resolve name from repository
                        if (existingCategoryId is not null && existingCategoryName is null)
                        {
                            try
                            {
                                var cat = await _categoryRepository.GetByIdAsync(existingCategoryId, CancellationToken.None);
                                existingCategoryName = cat?.Name;
                            }
                            catch (Exception ex)
                            {
                                SmartConLogger.Warn($"Failed to resolve category name for '{existingCategoryId}': {ex.Message}");
                            }
                        }
                        items.Add(new FamilyBatchImportItem(
                            path, SafeFileName.GetBaseName(path), sha256.Sha256, revitVersion, fileInfo.Length,
                            FamilyBatchImportStatus.Existing,
                            forcedExistingItemId ?? existingByName!.Id, existingByName?.CurrentVersionLabel,
                            existingCategoryId ?? categoryId, existingCategoryName ?? categoryName));
                    }
                    else
                    {
                        items.Add(new FamilyBatchImportItem(
                            path, SafeFileName.GetBaseName(path), sha256.Sha256, revitVersion, fileInfo.Length,
                            FamilyBatchImportStatus.New,
                            null, null, categoryId, categoryName));
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Failed to analyze file '{path}': {ex.Message}");
                    items.Add(new FamilyBatchImportItem(
                        path, SafeFileName.GetBaseName(path), string.Empty, 0, 0,
                        FamilyBatchImportStatus.Error));
                }
            }

            using var vm = new FamilyBatchImportViewModel(items, _dialogService, _viewModelFactory, _catalogProvider, categoryId);
            var result = _dialogService.ShowBatchImportDialog(vm);
            if (result != true) return;

            var selectedItems = vm.GetResultItems();
            var progress = new Progress<FamilyImportProgress>(p =>
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    p.CurrentFileIndex + 1, p.TotalFiles);
            });

            var importResult = await _importService.ImportBatchAsync(selectedItems, categoryId, progress, CancellationToken.None);

            // Extract types/attributes for successfully imported families
            var successfulItems = importResult.Results.Where(r => r.Success && !r.WasSkippedAsDuplicate).ToList();
            if (successfulItems.Count > 0)
            {
                _ = ExtractTypesForImportedFamilies(successfulItems, importResult.SuccessCount, importResult.SkippedCount, importResult.ErrorCount, importResult.TotalFiles);
            }
            else
            {
                StatusMessage = BuildImportStatusMessage(
                    importResult.SuccessCount, importResult.SkippedCount, importResult.ErrorCount, importResult.TotalFiles);
                await LoadTreeAsync();
            }

            await LoadTreeAsync();

            // Auto-clear status after 10 seconds
            FireAndForget(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                StatusMessage = string.Empty;
            }, nameof(ImportFilesAsync));
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Extracts types and attributes for successfully imported families via ExternalEvent.
    /// </summary>
    private async Task ExtractTypesForImportedFamilies(
        List<FamilyImportResult> importedItems,
        int successCount, int skippedCount, int errorCount, int total)
    {
        await _awaitableEvent.RaiseAsyncTask(async _ =>
        {
            // Update status inside ExternalEvent to avoid WPF render thread freeze
            // when OpenDocumentFile triggers MFC family upgrade dialog
            StatusMessage = BuildImportStatusMessage(successCount, skippedCount, errorCount, total);

            var extractionResults = new List<(string CatalogItemId, FamilyExtractionResult Result, string? VersionLabel, string? FileId, bool HasTypeCatalog)>();

            try
            {
                foreach (var item in importedItems)
                {
                    if (string.IsNullOrEmpty(item.CatalogItemId)) continue;
                    var catalogItemId = item.CatalogItemId!;

                    // ThreadPool: file resolution (SQLite/async)
                    var resolved = await _fileResolver
                        .ResolveForLoadAsync(catalogItemId, CurrentRevitVersion, CancellationToken.None)
                        .ConfigureAwait(true);

                    if (string.IsNullOrEmpty(resolved.AbsolutePath)) continue;

                    var txtPath = Path.ChangeExtension(resolved.AbsolutePath, ".txt");
                    var hasTypeCatalog = File.Exists(txtPath);

                    var extractionResult = _extractionService.Extract(resolved.AbsolutePath, Array.Empty<string>());
                    if (extractionResult.Success)
                    {
                        extractionResults.Add((catalogItemId, extractionResult, item.VersionId, item.FileId, hasTypeCatalog));
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"ExtractTypesForImportedFamilies extraction failed: {ex.Message}");
            }

            // FireAndForget: SQLite save + tree reload (non-critical post-processing)
            FireAndForget(async () =>
            {
                try
                {
                    foreach (var (catalogItemId, result, versionId, fileId, hasTypeCatalog) in extractionResults)
                    {
                        if (hasTypeCatalog)
                        {
                            await _dataImportService.MergeMissingValuesAsync(
                                catalogItemId, result, versionId, fileId, CancellationToken.None);
                        }
                        else
                        {
                            await _dataImportService.SaveExtractionResultAsync(
                                catalogItemId, result, versionId, fileId, CancellationToken.None);
                        }
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"ExtractTypesForImportedFamilies save failed: {ex.Message}");
                }
            }, nameof(ExtractTypesForImportedFamilies));
        });
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
    /// Persists the type names alongside the staged .rvt as
    /// <c>&lt;name&gt;.rvt.types.json</c>. The orchestrator reads this
    /// sidecar after the batch import completes, then the extractor
    /// uses the names to filter which types to read from the staged file.
    /// </summary>
    private static void WriteTypeSidecar(string rvtPath, IReadOnlyList<SelectedSystemType> types)
    {
        try
        {
            var metaPath = rvtPath + ".types.json";
            var typeNames = types.Select(t => t.Name).ToList();
            File.WriteAllText(metaPath, JsonSerializer.Serialize(typeNames));
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"[SystemImport.Create] Failed to write sidecar meta for '{Path.GetFileName(rvtPath)}': {ex.Message}");
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
        // I-01: StageSystemFromAnalysis and StageLoadableFamilyFromProject
        // both touch the Revit API (_systemFamilyIsolationProject.CreateCleanProjectWithTypesAndInstances
        // and EditFamily/SaveAs respectively). They must run on the Revit
        // UI thread. The WPF DockablePane SynchronizationContext is NOT
        // the Revit UI thread, so we route the whole build through
        // IFamilyManagerAwaitableEvent, which is bound to an ExternalEvent
        // handler. Mirrors the pattern in ExtractTypesForImportedFamilies
        // and AnalyzeActiveProjectAsync. We use the async overload
        // (RaiseAsync<Task<List<…>>>) so the caller's await unwraps the
        // inner task before returning the list.
        var inner = await _awaitableEvent.RaiseAsync(
            _ => BuildSelectedElementsBatchItemsCoreAsync(analysis),
            CancellationToken.None);
        return await inner;
    }

    private async Task<List<FamilyBatchImportItem>> BuildSelectedElementsBatchItemsCoreAsync(
        SelectedElementsAnalysis analysis)
    {
        var result = new List<FamilyBatchImportItem>();
        var ct = CancellationToken.None;

        // This method is invoked inside IFamilyManagerAwaitableEvent's
        // ExternalEvent handler, so the captured SynchronizationContext is
        // the Revit UI thread. ConfigureAwait(true) (the default) keeps us
        // on the Revit UI thread between awaits — required because the
        // foreach loop below calls StageLoadableFamilyFromProject (Revit
        // API) on every iteration. ConfigureAwait(false) would resume the
        // loop on the thread pool and crash the next EditFamily call.
        //
        // Load all categories once and reuse the dictionary for every row
        // builder. Eliminates an N+1 query pattern (each GetByIdAsync was
        // doing one targeted SELECT plus one full table scan to build the
        // FullPath; with 42+ families this turned into ~84 SQL round-trips).
        // We populate the dictionary with whatever the categories table
        // currently has — a row's "real" category is identified by
        // CategoryId, and the dictionary is the single source of truth for
        // the display label (FullPath). This is the same lookup the
        // legacy ShowBatchImportDialogAsync used to do via
        // _categoryRepository.GetByIdAsync, but amortised across the batch.
        var allCategories = await _categoryRepository.GetAllAsync(ct).ConfigureAwait(true);
        var categoriesById = allCategories.ToDictionary(c => c.Id);

        var pendingItems = StageSystemFromAnalysis(analysis);
        foreach (var pending in pendingItems)
        {
            if (!File.Exists(pending.TempRvtPath))
                continue;

            var fileInfo = new FileInfo(pending.TempRvtPath);
            var revitVersion = _fileInfoReader.ReadRevitVersion(pending.TempRvtPath) ?? CurrentRevitVersion;

            var metadata = await _metadataService.ExtractAsync(pending.TempRvtPath, ct);
            var sha256 = metadata.Sha256;

            var existingByHash = await _catalogProvider.FindByHashAsync(sha256, ct);
            var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(pending.CategoryName);
            var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, ct);

            FamilyBatchImportStatus status;
            string? existingId = null;
            string? existingVersionLabel = null;

            if (existingByHash is not null)
            {
                status = FamilyBatchImportStatus.Duplicate;
                existingId = existingByHash.CatalogItemId;
                existingVersionLabel = existingByHash.VersionLabel;
            }
            else if (existingByName is not null)
            {
                status = FamilyBatchImportStatus.Existing;
                existingId = existingByName.Id;
                existingVersionLabel = existingByName.CurrentVersionLabel;
            }
            else
            {
                status = FamilyBatchImportStatus.New;
            }

            result.Add(new FamilyBatchImportItem(
                FilePath: pending.TempRvtPath,
                FileName: pending.CategoryName,
                Sha256: sha256,
                RevitMajorVersion: revitVersion,
                FileSizeBytes: fileInfo.Length,
                Status: status,
                ExistingCatalogItemId: existingId,
                ExistingVersionLabel: existingVersionLabel,
                TargetCategoryId: null,
                TargetCategoryName: null,
                FamilySource: "system",
                TypeCount: pending.Types.Count,
                RevitCategory: pending.CategoryName));
        }

        foreach (var loadable in analysis.LoadableFamilies)
        {
            var rfaPath = StageLoadableFamilyFromProject(loadable);
            if (string.IsNullOrEmpty(rfaPath) || !File.Exists(rfaPath))
                continue;

            var fileInfo = new FileInfo(rfaPath);
            var revitVersion = _fileInfoReader.ReadRevitVersion(rfaPath!) ?? CurrentRevitVersion;
            var metadata = await _metadataService.ExtractAsync(rfaPath!, ct);
            var sha256 = metadata.Sha256;

            var stagedBaseName = SafeFileName.GetBaseName(rfaPath!);
            var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(stagedBaseName);
            var existingByHash = await _catalogProvider.FindByHashAsync(sha256, ct);
            var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, ct);

            FamilyBatchImportStatus status;
            string? existingId = null;
            string? existingVersionLabel = null;
            string? targetCategoryId = null;
            string? targetCategoryName = null;

            if (existingByHash is not null)
            {
                status = FamilyBatchImportStatus.Duplicate;
                existingId = existingByHash.CatalogItemId;
                existingVersionLabel = existingByHash.VersionLabel;
            }
            else if (existingByName is not null)
            {
                status = FamilyBatchImportStatus.Existing;
                existingId = existingByName.Id;
                existingVersionLabel = existingByName.CurrentVersionLabel;
                // Inherit the existing item's category so the batch dialog
                // pre-fills the correct "Целевая категория" cell instead of
                // defaulting to "Без категории" for already-categorised items.
                targetCategoryId = existingByName.CategoryId;
                targetCategoryName = existingByName.CategoryPath;
            }
            else
            {
                status = FamilyBatchImportStatus.New;
            }

            // Resolve the display label from the categories table whenever
            // we have a real CategoryId. We do NOT trust the denormalised
            // CategoryPath stored in catalog_items (see the matching comment
            // in BuildLoadableFamilyBatchRowAsync for the full rationale):
            // a stale or placeholder "Без категории" in the DB would otherwise
            // mask a real category assignment. Use FullPath (or Name as
            // fallback) from the categories table as the source of truth.
            //
            // Performance: uses the categoriesById dictionary pre-loaded at
            // the top of this method (one DB round-trip for the whole batch
            // instead of N+1 queries per row).
            if (targetCategoryId is not null && categoriesById.TryGetValue(targetCategoryId, out var cat) && cat is not null)
            {
                targetCategoryName = cat.FullPath ?? cat.Name;
            }

            result.Add(new FamilyBatchImportItem(
                FilePath: rfaPath!,
                FileName: stagedBaseName,
                Sha256: sha256,
                RevitMajorVersion: revitVersion,
                FileSizeBytes: fileInfo.Length,
                Status: status,
                ExistingCatalogItemId: existingId,
                ExistingVersionLabel: existingVersionLabel,
                TargetCategoryId: targetCategoryId,
                TargetCategoryName: targetCategoryName,
                FamilySource: "loadable",
                TypeCount: loadable.TypeCount,
                RevitCategory: loadable.CategoryName,
                OriginalSourcePath: null));
        }

        return result;
    }

    private List<SystemFamilyPendingImport> StageSystemFromAnalysis(SelectedElementsAnalysis analysis)
    {
        var pending = new List<SystemFamilyPendingImport>();
        if (analysis.SystemTypes.Count == 0) return pending;

        var activeDoc = _revitContext.GetDocument();
        if (activeDoc is null) return pending;

        var groups = analysis.SystemTypes
            .GroupBy(s => s.Category)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var category = group.Key;
            var types = group.ToList();
            var uniqueIds = types.Select(t => t.UniqueId).ToList();
            var displayName = types[0].CategoryName;

            var createResult = _systemFamilyIsolationProject.CreateCleanProjectWithTypesAndInstances(
                activeDoc, uniqueIds, category, displayName);
            if (!createResult.Success || string.IsNullOrEmpty(createResult.FilePath))
                continue;

            WriteTypeSidecar(createResult.FilePath!, types);

            pending.Add(new SystemFamilyPendingImport(displayName, types, createResult.FilePath!));
        }

        return pending;
    }

    private async Task<List<FamilyBatchImportItem>> BuildActiveProjectBatchItemsAsync(
        IReadOnlyList<CategoryAnalysis> systemAnalyses,
        IReadOnlyList<LoadableFamilyInfo> loadableFamilies)
    {
        // I-01: see the matching comment in BuildSelectedElementsBatchItemsAsync.
        // Route the whole build through IFamilyManagerAwaitableEvent so the
        // Revit-API calls in _systemFamilyIsolationProject.CreateCleanProjectWithTypesAndInstances
        // and StageLoadableFamilyFromProject (EditFamily/SaveAs) run on the
        // Revit UI thread, not the WPF dispatcher thread that resumed after
        // the DockablePane async flow.
        var inner = await _awaitableEvent.RaiseAsync(
            _ => BuildActiveProjectBatchItemsCoreAsync(systemAnalyses, loadableFamilies),
            CancellationToken.None);
        return await inner;
    }

    private async Task<List<FamilyBatchImportItem>> BuildActiveProjectBatchItemsCoreAsync(
        IReadOnlyList<CategoryAnalysis> systemAnalyses,
        IReadOnlyList<LoadableFamilyInfo> loadableFamilies)
    {
        var result = new List<FamilyBatchImportItem>();
        var ct = CancellationToken.None;

        // Load all categories once. See BuildSelectedElementsBatchItemsAsync
        // for the full rationale (N+1 elimination + source-of-truth for the
        // display label, and ConfigureAwait(true) so we keep resuming on
        // the Revit UI thread between awaits — required by the
        // StageLoadableFamilyFromProject calls below).
        var allCategories = await _categoryRepository.GetAllAsync(ct).ConfigureAwait(true);
        var categoriesById = allCategories.ToDictionary(c => c.Id);

        if (systemAnalyses.Count > 0)
        {
            var activeDoc = _revitContext.GetDocument();
            if (activeDoc is not null)
            {
                var singleCategory = systemAnalyses.Count == 1;
                var sourceName = singleCategory ? ResolveActiveProjectDisplayName(activeDoc) : null;

                foreach (var analysis in systemAnalyses)
                {
                    var types = analysis.Types
                        .Select(t => new SelectedSystemType(
                            t.UniqueId, t.Name, analysis.DisplayName, analysis.Category))
                        .ToList();
                    var uniqueIds = types.Select(t => t.UniqueId).ToList();
                    var displayName = singleCategory ? sourceName! : analysis.DisplayName;

                    var createResult = _systemFamilyIsolationProject.CreateCleanProjectWithTypesAndInstances(
                        activeDoc, uniqueIds, analysis.Category, displayName);
                    if (!createResult.Success || string.IsNullOrEmpty(createResult.FilePath))
                        continue;

                    WriteTypeSidecar(createResult.FilePath!, types);

                    var pending = new SystemFamilyPendingImport(displayName, types, createResult.FilePath!);
                    var row = await BuildSystemFamilyBatchRowAsync(pending, ct, categoriesById);
                    if (row is not null) result.Add(row);
                }
            }
        }

        if (loadableFamilies.Count > 0)
        {
            foreach (var loadable in loadableFamilies)
            {
                var rfaPath = StageLoadableFamilyFromProject(loadable);
                if (string.IsNullOrEmpty(rfaPath) || !File.Exists(rfaPath)) continue;

                var row = await BuildLoadableFamilyBatchRowAsync(loadable, rfaPath!, ct, categoriesById);
                if (row is not null) result.Add(row);
            }
        }

        return result;
    }

    private async Task<FamilyBatchImportItem?> BuildSystemFamilyBatchRowAsync(
        SystemFamilyPendingImport pending, CancellationToken ct,
        IReadOnlyDictionary<string, CategoryNode>? categoriesById = null)
    {
        if (!File.Exists(pending.TempRvtPath)) return null;

        var fileInfo = new FileInfo(pending.TempRvtPath);
        var revitVersion = _fileInfoReader.ReadRevitVersion(pending.TempRvtPath) ?? CurrentRevitVersion;
        var metadata = await _metadataService.ExtractAsync(pending.TempRvtPath, ct);
        var sha256 = metadata.Sha256;
        var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(pending.CategoryName);
        var existingByHash = await _catalogProvider.FindByHashAsync(sha256, ct);
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, ct);

        FamilyBatchImportStatus status;
        string? existingId = null;
        string? existingVersionLabel = null;
        string? targetCategoryId = null;
        string? targetCategoryName = null;

        if (existingByHash is not null)
        {
            status = FamilyBatchImportStatus.Duplicate;
            existingId = existingByHash.CatalogItemId;
            existingVersionLabel = existingByHash.VersionLabel;
            // NOTE: FamilyCatalogVersion does not carry CategoryId/Path.
            // For Duplicate status the dialog only shows Action=Skip, so
            // the target category is informational only; we leave it
            // null and rely on existingByName to pre-fill for Existing
            // (more common) status. Mirrors the legacy
            // ShowBatchImportDialogAsync behaviour.
        }
        else if (existingByName is not null)
        {
            status = FamilyBatchImportStatus.Existing;
            existingId = existingByName.Id;
            existingVersionLabel = existingByName.CurrentVersionLabel;
            // Inherit the existing item's category so the batch dialog
            // pre-fills the correct "Целевая категория" cell instead of
            // defaulting to "Без категории" for already-categorised items.
            targetCategoryId = existingByName.CategoryId;
            targetCategoryName = existingByName.CategoryPath;
        }
        else
        {
            status = FamilyBatchImportStatus.New;
        }

        // Resolve the display label from the categories table whenever
        // we have a real CategoryId. We do NOT trust the denormalised
        // CategoryPath stored in catalog_items (see the matching comment
        // in BuildLoadableFamilyBatchRowAsync for the full rationale):
        // a stale or placeholder "Без категории" in the DB would otherwise
        // mask a real category assignment. Use FullPath (or Name as
        // fallback) from the categories table as the source of truth.
        //
        // Performance: when called from BuildSelectedElementsBatchItemsAsync
        // we receive a pre-loaded dictionary of all categories (one DB
        // round-trip for the whole batch). When called standalone (single
        // row), we fall back to a targeted GetByIdAsync query.
        if (targetCategoryId is not null)
        {
            CategoryNode? cat = null;
            if (categoriesById is not null)
            {
                categoriesById.TryGetValue(targetCategoryId, out cat);
            }
            else
            {
                try
                {
                    cat = await _categoryRepository.GetByIdAsync(targetCategoryId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Failed to resolve category name for existing system item '{existingId}': {ex.Message}");
                }
            }
            if (cat is not null)
            {
                targetCategoryName = cat.FullPath ?? cat.Name;
            }
        }

        return new FamilyBatchImportItem(
            FilePath: pending.TempRvtPath,
            FileName: pending.CategoryName,
            Sha256: sha256,
            RevitMajorVersion: revitVersion,
            FileSizeBytes: fileInfo.Length,
            Status: status,
            ExistingCatalogItemId: existingId,
            ExistingVersionLabel: existingVersionLabel,
            TargetCategoryId: targetCategoryId,
            TargetCategoryName: targetCategoryName,
            FamilySource: "system",
            TypeCount: pending.Types.Count,
            RevitCategory: pending.CategoryName);
    }

    private async Task<FamilyBatchImportItem?> BuildLoadableFamilyBatchRowAsync(
        LoadableFamilyInfo loadable, string rfaPath, CancellationToken ct,
        IReadOnlyDictionary<string, CategoryNode>? categoriesById = null)
    {
        var fileInfo = new FileInfo(rfaPath);
        var revitVersion = _fileInfoReader.ReadRevitVersion(rfaPath) ?? CurrentRevitVersion;
        var metadata = await _metadataService.ExtractAsync(rfaPath, ct);
        var sha256 = metadata.Sha256;

        var stagedBaseName = SafeFileName.GetBaseName(rfaPath);
        var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(stagedBaseName);
        var existingByHash = await _catalogProvider.FindByHashAsync(sha256, ct);
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, ct);

        // Diagnostic: trace what we found for the dialog target category.
        // Debug-level because batch import can stage dozens of loadable
        // families and an Info per row would dominate the log. Kept as
        // Debug so it can be enabled with SmartConLogger.DebugEnabled for
        // field debugging without polluting production logs.
        SmartConLogger.Debug(
            $"file='{stagedBaseName}' normalized='{normalizedName}' " +
            $"byHash={(existingByHash is null ? "null" : "hit")} " +
            $"byName={(existingByName is null ? "null" : $"Id={existingByName.Id} CatId={existingByName.CategoryId ?? "<null>"} CatPath={existingByName.CategoryPath ?? "<null>"}")}");

        FamilyBatchImportStatus status;
        string? existingId = null;
        string? existingVersionLabel = null;
        string? targetCategoryId = null;
        string? targetCategoryName = null;

        if (existingByHash is not null)
        {
            status = FamilyBatchImportStatus.Duplicate;
            existingId = existingByHash.CatalogItemId;
            existingVersionLabel = existingByHash.VersionLabel;
            // NOTE: FamilyCatalogVersion does not carry CategoryId/Path.
            // For Duplicate status the dialog only shows Action=Skip, so
            // the target category is informational only; we leave it
            // null and rely on existingByName to pre-fill for Existing
            // (more common) status. Mirrors the legacy
            // ShowBatchImportDialogAsync behaviour.
        }
        else if (existingByName is not null)
        {
            status = FamilyBatchImportStatus.Existing;
            existingId = existingByName.Id;
            existingVersionLabel = existingByName.CurrentVersionLabel;
            // Inherit the existing item's category so the batch dialog
            // pre-fills the correct "Целевая категория" cell instead of
            // defaulting to "Без категории" for already-categorised items.
            targetCategoryId = existingByName.CategoryId;
            targetCategoryName = existingByName.CategoryPath;
        }
        else
        {
            status = FamilyBatchImportStatus.New;
        }

        // Resolve the display label from the categories table whenever
        // we have a real CategoryId. We do NOT trust the denormalised
        // CategoryPath stored in catalog_items, because picker wrote the
        // literal "Без категории" placeholder into that column whenever the
        // user picked "no category" in the picker. If we trusted the
        // denormalised value here, every already-categorised family would
        // be displayed as "Без категории" in the batch dialog and the user
        // would see a wall of false placeholders for items that DO have
        // a real category assigned in the tree. Use FullPath (or Name as
        // fallback) from the categories table as the single source of truth.
        //
        // Performance: when called from BuildSelectedElementsBatchItemsAsync
        // we receive a pre-loaded dictionary of all categories (one DB
        // round-trip for the whole batch). When called standalone (single
        // row), we fall back to a targeted GetByIdAsync query.
        if (targetCategoryId is not null)
        {
            CategoryNode? cat = null;
            if (categoriesById is not null)
            {
                categoriesById.TryGetValue(targetCategoryId, out cat);
            }
            else
            {
                try
                {
                    cat = await _categoryRepository.GetByIdAsync(targetCategoryId, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Failed to resolve category name for existing item '{existingId}': {ex.Message}");
                }
            }
            if (cat is not null)
            {
                targetCategoryName = cat.FullPath ?? cat.Name;
            }
        }

        return new FamilyBatchImportItem(
            FilePath: rfaPath,
            FileName: stagedBaseName,
            Sha256: sha256,
            RevitMajorVersion: revitVersion,
            FileSizeBytes: fileInfo.Length,
            Status: status,
            ExistingCatalogItemId: existingId,
            ExistingVersionLabel: existingVersionLabel,
            TargetCategoryId: targetCategoryId,
            TargetCategoryName: targetCategoryName,
            FamilySource: "loadable",
            TypeCount: loadable.TypeCount,
            RevitCategory: loadable.CategoryName,
            OriginalSourcePath: null);
    }

    private string? StageLoadableFamilyFromProject(LoadableFamilyInfo info)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null) return null;

        var family = doc.GetElement(info.FamilyUniqueId) as Autodesk.Revit.DB.Family;
        if (family is null)
        {
            SmartConLogger.Warn(
                $"Family '{info.FamilyName}' (uid='{info.FamilyUniqueId}') not found in active project");
            return null;
        }
        if (family.IsInPlace)
        {
            SmartConLogger.Warn(
                $"Skipping in-place family '{info.FamilyName}'");
            return null;
        }

        Document? familyDoc = null;
        try
        {
            familyDoc = doc.EditFamily(family);
            if (familyDoc is null || !familyDoc.IsFamilyDocument)
            {
                SmartConLogger.Warn(
                    $"EditFamily returned null/non-family for '{info.FamilyName}'");
                return null;
            }

            // info.FamilyName from LoadableFamilyInfo is the full family name
            // (Revit API returns the complete string with internal dots preserved,
            // e.g. "BP_A0307_ITAP_ART.162_Амер угловая"). SafeFileName.GetBaseName
            // is a no-op for strings without a known extension, so it returns the
            // input unchanged — no double truncation.
            var safeName = SanitizeFileName(SafeFileName.GetBaseName(info.FamilyName));
            var guid = Guid.NewGuid().ToString("N");
            var dir = Path.Combine(
                Path.GetTempPath(),
                Core.Services.FamilyManager.SystemFamilyTempLayout.TempRoot,
                Core.Services.FamilyManager.SystemFamilyTempLayout.StagingSubdir,
                guid);
            Directory.CreateDirectory(dir);
            var rfaPath = Path.Combine(dir, safeName + ".rfa");

            familyDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
            SmartConLogger.Debug(
                $"Staged '{info.FamilyName}' → '{rfaPath}'");
            return rfaPath;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Failed to stage '{info.FamilyName}': {ex.Message}");
            return null;
        }
        finally
        {
            if (familyDoc is not null)
            {
                try { familyDoc.Close(false); } catch { }
                // Defensive ReleaseComObject — required for batch processing of
                // 100+ families to prevent family-upgrade freeze (REVIT-237190).
                // Document is a RCW; without explicit release the runtime keeps
                // a reference until GC, which can hang Revit on shutdown.
                try { Marshal.ReleaseComObject(familyDoc); } catch { }
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}

