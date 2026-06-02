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
                            path, Path.GetFileName(path), sha256.Sha256, revitVersion, fileInfo.Length,
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
                            path, Path.GetFileName(path), sha256.Sha256, revitVersion, fileInfo.Length,
                            FamilyBatchImportStatus.Existing,
                            forcedExistingItemId ?? existingByName!.Id, existingByName?.CurrentVersionLabel,
                            existingCategoryId ?? categoryId, existingCategoryName ?? categoryName));
                    }
                    else
                    {
                        items.Add(new FamilyBatchImportItem(
                            path, Path.GetFileName(path), sha256.Sha256, revitVersion, fileInfo.Length,
                            FamilyBatchImportStatus.New,
                            null, null, categoryId, categoryName));
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Failed to analyze file '{path}': {ex.Message}");
                    items.Add(new FamilyBatchImportItem(
                        path, Path.GetFileName(path), string.Empty, 0, 0,
                        FamilyBatchImportStatus.Error));
                }
            }

            using var vm = new FamilyBatchImportViewModel(items, _dialogService, _viewModelFactory, categoryId);
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
                ExtractTypesForImportedFamilies(successfulItems, importResult.SuccessCount, importResult.SkippedCount, importResult.ErrorCount, importResult.TotalFiles);
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
    private void ExtractTypesForImportedFamilies(
        List<FamilyImportResult> importedItems,
        int successCount, int skippedCount, int errorCount, int total)
    {
        _externalEvent.Raise(() =>
        {
            // Update status inside ExternalEvent to avoid WPF render thread freeze
            // when OpenDocumentFile triggers MFC family upgrade dialog
            StatusMessage = BuildImportStatusMessage(successCount, skippedCount, errorCount, total);

            var extractionResults = new List<(string CatalogItemId, FamilyExtractionResult Result, string? VersionLabel, string? FileId)>();

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

                    // Skip extraction if Type Catalog (.txt) exists — types already imported from catalog
                    var txtPath = Path.ChangeExtension(resolved.AbsolutePath, ".txt");
                    if (File.Exists(txtPath))
                    {
                        SmartConLogger.Info($"[ExtractTypes] Skipping extraction for '{catalogItemId}' — Type Catalog found at {txtPath}");
                        continue;
                    }

                    // UI thread: Revit API (OpenDocumentFile + Extract)
                    var extractionResult = _extractionService.Extract(resolved.AbsolutePath, Array.Empty<string>());
                    if (extractionResult.Success)
                    {
                        extractionResults.Add((catalogItemId, extractionResult, item.VersionLabel, item.FileId));
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
                    foreach (var (catalogItemId, result, versionLabel, fileId) in extractionResults)
                    {
                        await _dataImportService.SaveExtractionResultAsync(
                            catalogItemId, result, versionLabel, fileId, CancellationToken.None);
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
}
