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

    [RelayCommand(CanExecute = nameof(CanImportToCategoryWithAccess))]
    private async Task ImportFolderToCategoryAsync()
    {
        if (SelectedTreeNode is not CategoryNodeViewModel categoryNode) return;
        if (categoryNode.CategoryId == "__no_category__") return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFolder) ?? "Import Folder";
        var path = _dialogService.ShowFolderBrowserDialog(title);
        if (string.IsNullOrWhiteSpace(path)) return;

        var files = Directory.GetFiles(path!, "*.rfa", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            StatusMessage = "No .rfa files found in selected folder";
            return;
        }

        await ShowBatchImportDialogAsync(files, categoryNode.CategoryId);
    }

    [RelayCommand(CanExecute = nameof(CanImportToCategoryWithAccess))]
    private void ImportDataForCategory()
    {
        if (SelectedTreeNode is not CategoryNodeViewModel categoryNode) return;
        if (categoryNode.CategoryId == "__no_category__") return;

        var families = CollectFamiliesRecursive(categoryNode);
        if (families.Count == 0)
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoFamiliesInCategory) ?? "No families in category";
            return;
        }

        _externalEvent.Raise(() =>
        {
            try
            {
                IsLoading = true;
                StatusMessage = string.Empty;
                
                var preparedItems = new List<(string CatalogItemId, string Name, string? FilePath, IReadOnlyList<string> ParamNames, string? VersionId)>();
                var targetRevit = CurrentRevitVersion;

                for (var i = 0; i < families.Count; i++)
                {
                    var family = families[i];
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportDataProgress) ?? "Preparing {0} of {1}: {2}",
                        i + 1, families.Count, family.DisplayName);

                    var prepareResult = Task.Run(() => _dataImportService.PrepareExtractionAsync(
                        family.CatalogItemId, targetRevit, CancellationToken.None)).GetAwaiter().GetResult();
                    
                    if (!prepareResult.Success || string.IsNullOrEmpty(prepareResult.ResolvedFilePath))
                        continue;

                    preparedItems.Add((
                        family.CatalogItemId,
                        family.DisplayName,
                        prepareResult.ResolvedFilePath,
                        prepareResult.ParameterNames,
                        prepareResult.Item?.CurrentVersionLabel));
                }

                if (preparedItems.Count == 0)
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Preparation error";
                    IsLoading = false;
                    return;
                }

                var successCount = 0;
                var errorCount = 0;

                for (var i = 0; i < preparedItems.Count; i++)
                {
                    var item = preparedItems[i];
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportDataProgress) ?? "Processing {0} of {1}: {2}",
                        i + 1, preparedItems.Count, item.Name);

                    try
                    {
                        var result = _extractionService.Extract(item.FilePath!, item.ParamNames);
                        
                        if (result.Success)
                        {
                            var saveResult = Task.Run(() => _dataImportService.SaveExtractionResultAsync(
                                item.CatalogItemId, result, item.VersionId, null, CancellationToken.None)).GetAwaiter().GetResult();
                            
                            if (saveResult.Success)
                                successCount++;
                            else
                                errorCount++;
                        }
                        else
                        {
                            errorCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        SmartConLogger.Warn($"ImportData failed for {item.Name}: {ex.Message}");
                    }
                }

                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportDataResultFormat) ?? "Imported: {0} types, {1} values found",
                    $"{successCount}/{preparedItems.Count} families", "see log");

                IsLoading = false;
                
                FireAndForget(() => LoadTreeAsync());
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"ImportDataForCategory failed: {ex.Message}");
                StatusMessage = ex.Message;
                IsLoading = false;
            }
        });
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
                            existingByHash.CatalogItemId, existingByHash.VersionLabel));
                        continue;
                    }

                    // Check by normalized name
                    var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(Path.GetFileNameWithoutExtension(path));
                    var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None);
                    if (existingByName is not null || forcedExistingItemId is not null)
                    {
                        var existingCategoryId = existingByName?.CategoryId;
                        var existingCategoryName = existingByName?.CategoryPath;
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

    private static List<FamilyLeafNodeViewModel> CollectFamiliesRecursive(CategoryNodeViewModel categoryNode)
    {
        var result = new List<FamilyLeafNodeViewModel>();
        foreach (var child in categoryNode.Children)
        {
            if (child is FamilyLeafNodeViewModel leaf)
                result.Add(leaf);
            else if (child is CategoryNodeViewModel cat)
                result.AddRange(CollectFamiliesRecursive(cat));
        }
        return result;
    }

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
