using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private async Task ImportFilesAsync()
    {
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
                            path, Path.GetFileNameWithoutExtension(path), sha256.Sha256, revitVersion, fileInfo.Length,
                            FamilyBatchImportStatus.Duplicate,
                            existingByHash.CatalogItemId, existingByHash.VersionLabel,
                            categoryId, categoryName));
                        continue;
                    }

                    // Check by normalized name
                    var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(Path.GetFileNameWithoutExtension(path));
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
                                SmartConLogger.Warn($"[BatchImport] Failed to resolve category name for '{existingCategoryId}': {ex.Message}");
                            }
                        }
                        items.Add(new FamilyBatchImportItem(
                            path, Path.GetFileNameWithoutExtension(path), sha256.Sha256, revitVersion, fileInfo.Length,
                            FamilyBatchImportStatus.Existing,
                            forcedExistingItemId ?? existingByName!.Id, existingByName?.CurrentVersionLabel,
                            existingCategoryId ?? categoryId, existingCategoryName ?? categoryName));
                    }
                    else
                    {
                        items.Add(new FamilyBatchImportItem(
                            path, Path.GetFileNameWithoutExtension(path), sha256.Sha256, revitVersion, fileInfo.Length,
                            FamilyBatchImportStatus.New,
                            null, null, categoryId, categoryName));
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Failed to analyze file '{path}': {ex.Message}");
                    items.Add(new FamilyBatchImportItem(
                        path, Path.GetFileNameWithoutExtension(path), string.Empty, 0, 0,
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
            });
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
        await _awaitableEvent.RaiseAsync(_ =>
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
                    var resolved = Task.Run(() =>
                        _fileResolver.ResolveForLoadAsync(catalogItemId, CurrentRevitVersion, CancellationToken.None))
                        .GetAwaiter().GetResult();

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

                await LoadTreeAsync();
            });
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
    private async Task ImportSystemFamilyAsync()
    {
        IsLoading = true;
        StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilySelectPrompt)
            ?? "Выберите элементы системного семейства в Revit...";

        IReadOnlyList<SystemFamilyPendingImport>? pendingItems = null;

        await _awaitableEvent.RaiseAsync(_ =>
        {
            try
            {
                var doc = _revitContext.GetDocument();
                if (doc is null || doc.IsFamilyDocument)
                {
                    _dialogService.ShowWarning(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportSystemFamily) ?? "Импорт системного семейства",
                        LanguageManager.GetString(StringLocalization.Keys.FM_ActiveDocNotProject)
                            ?? "Активный документ не является проектом. Откройте проект Revit.");
                    IsLoading = false;
                    return;
                }

                pendingItems = _systemFamilyImportService.PickAndPrepare();
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Ошибка импорта: {0}",
                    ex.Message);
                IsLoading = false;
            }
        });

        if (pendingItems is null || pendingItems.Count == 0)
        {
            if (pendingItems is not null)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImportFailed)
                    ?? "Не удалось подготовить системные семейства";
                IsLoading = false;
            }
            return;
        }

        try
        {
            var batchItems = await BuildSystemFamilyBatchItemsAsync(pendingItems);

            if (batchItems.Count == 0)
            {
                CleanupTempFiles(pendingItems);
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImportFailed)
                    ?? "Не удалось подготовить системные семейства";
                IsLoading = false;
                return;
            }

            IReadOnlyList<FamilyBatchImportItem> selectedItems;
            using (var batchVm = new FamilyBatchImportViewModel(batchItems, _dialogService, _viewModelFactory, _catalogProvider))
            {
                var dialogResult = _dialogService.ShowBatchImportDialog(batchVm);
                if (dialogResult != true)
                {
                    CleanupTempFiles(pendingItems);
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Cancel)
                        ?? "Отменено";
                    IsLoading = false;
                    return;
                }
                selectedItems = batchVm.GetResultItems();
            }

            var toImport = selectedItems.Where(i => i.Action != FamilyBatchImportAction.Skip).ToList();

            if (toImport.Count == 0)
            {
                CleanupTempFiles(pendingItems);
                var skipped = selectedItems.Count;
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportStatusSkipped) ?? "пропущено: {0}",
                    skipped);
                IsLoading = false;
                return;
            }

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyPreparing)
                    ?? "Импорт {0} системных семейств...",
                toImport.Count);

            var result = await _systemFamilyImportService.ImportBatchItemsAsync(toImport);

            if (result.ExtractionTasks.Count > 0)
            {
                await ExtractSystemFamilyAttributesAsync(result.ExtractionTasks);
            }

            StatusMessage = result.Success
                ? string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImported)
                        ?? "Импортировано системных семейств: {0}",
                    result.TypesCount)
                : result.Message ?? (LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImportFailed)
                    ?? "Ошибка импорта системного семейства");

            if (result.Success)
                await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[SystemImport] ImportSystemFamily failed: {ex.Message}");
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
    /// Extracts Type parameters from each staged .rvt and persists the
    /// result to the catalog. Awaits the in-flight saves so callers can
    /// rely on ordering (e.g. <i>extract</i> → <i>save</i> → <i>cleanup</i>).
    /// </summary>
    private async Task ExtractSystemFamilyAttributesAsync(
        IReadOnlyList<SystemFamilyExtractionTask> tasks)
    {
        if (tasks.Count == 0) return;

        SmartConLogger.Debug(
            $"[SystemImport] Awaiting extraction for {tasks.Count} .rvt task(s) via AwaitableEvent...");

        // Captured inside the UI thread callback, awaited outside.
        var pendingSaves = new List<Task>();

        await _awaitableEvent.RaiseAsync(_ =>
        {
            foreach (var task in tasks)
            {
                try
                {
                    if (!File.Exists(task.TempRvtPath))
                    {
                        SmartConLogger.Warn(
                            $"[SystemImport] Temp .rvt not found for extraction: {task.TempRvtPath}");
                        continue;
                    }

                    var extraction = _systemFamilyAttributeExtraction.ExtractFromRvt(
                        task.TempRvtPath, task.TypeNames);
                    if (extraction.Success)
                    {
                        var saveTask = Task.Run(async () =>
                        {
                            try
                            {
                                await _dataImportService.SaveExtractionResultAsync(
                                    task.CatalogItemId, extraction, task.VersionId, task.FileId,
                                    CancellationToken.None);
                                SmartConLogger.Debug(
                                    $"[SystemImport] Saved extraction for '{task.TempRvtPath}': " +
                                    $"{extraction.Types.Count} types");
                            }
                            catch (Exception ex)
                            {
                                SmartConLogger.Warn(
                                    $"[SystemImport] SaveExtractionResult failed: {ex.Message}");
                            }
                        });
                        pendingSaves.Add(saveTask);
                    }
                    else
                    {
                        SmartConLogger.Warn(
                            $"[SystemImport] Extraction failed for '{task.TempRvtPath}': " +
                            $"{extraction.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"[SystemImport] Extraction exception for '{task.TempRvtPath}': {ex.Message}");
                }
            }
        });

        // Wait for all saves to complete BEFORE deleting the temp files.
        // This guarantees the cleanup never races with an in-flight save.
        if (pendingSaves.Count > 0)
        {
            SmartConLogger.Debug(
                $"[SystemImport] Waiting for {pendingSaves.Count} save(s) before cleanup...");
            try
            {
                await Task.WhenAll(pendingSaves);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"[SystemImport] One or more saves failed: {ex.Message}");
            }
        }

        foreach (var task in tasks)
        {
            try
            {
                if (File.Exists(task.TempRvtPath)) File.Delete(task.TempRvtPath);
                var metaPath = task.TempRvtPath + ".types.json";
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
            catch { }
        }

        SmartConLogger.Info(
            $"[SystemImport] ✓ Extraction phase complete ({pendingSaves.Count} file(s) saved)");
    }

    private async Task<List<FamilyBatchImportItem>> BuildSystemFamilyBatchItemsAsync(
        IReadOnlyList<SystemFamilyPendingImport> pendingItems)
    {
        var result = new List<FamilyBatchImportItem>();
        var ct = CancellationToken.None;

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

            SmartConLogger.Info($"[SystemImport] Batch row: {pending.CategoryName}, Status: {status}, Sha256: {sha256[..Math.Min(16, sha256.Length)]}..., Revit: R{revitVersion}, Size: {fileInfo.Length}, TypeCount: {pending.Types.Count}");

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

        return result;
    }

    private static void CleanupTempFiles(IReadOnlyList<SystemFamilyPendingImport> pendingItems)
    {
        foreach (var p in pendingItems)
        {
            try
            {
                if (File.Exists(p.TempRvtPath)) File.Delete(p.TempRvtPath);
                var metaPath = p.TempRvtPath + ".types.json";
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
            catch { }
        }
    }
}
