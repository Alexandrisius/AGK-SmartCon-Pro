using System.Collections.ObjectModel;
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

        IsLoading = true;
        try
        {
            var successCount = 0;
            var skipCount = 0;
            var errorCount = 0;

            for (var i = 0; i < paths.Length; i++)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    i + 1, paths.Length);

                var request = new FamilyImportRequest(paths[i], CurrentRevitVersion, null, null, null);
                var result = await _importService.ImportFileAsync(request);

                if (result.WasSkippedAsDuplicate) skipCount++;
                else if (result.Success) successCount++;
                else errorCount++;
            }

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportSuccess) ?? "Imported: {0}",
                $"{successCount} / {paths.Length}");

            await LoadTreeAsync();
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

    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private async Task ImportFolderAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFolder) ?? "Import Folder";
        var path = _dialogService.ShowFolderBrowserDialog(title);
        if (string.IsNullOrWhiteSpace(path)) return;

        await ImportFolder(path!);
    }

    private async Task ImportFile(string path)
    {
        IsLoading = true;
        try
        {
            var request = new FamilyImportRequest(path, CurrentRevitVersion, null, null, null);
            var result = await _importService.ImportFileAsync(request);

            if (result.WasSkippedAsDuplicate)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DuplicateSkipped) ?? "Duplicate skipped: {0}",
                    result.FileName);
            }
            else if (result.Success)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportSuccess) ?? "Imported: {0}",
                    result.FileName);
            }
            else
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                    result.ErrorMessage);
            }

            await LoadTreeAsync();
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

    private async Task ImportFolder(string path)
    {
        IsLoading = true;
        try
        {
            var request = new FamilyFolderImportRequest(path, CurrentRevitVersion, true, null, null, null);
            var progress = new Progress<FamilyImportProgress>(p =>
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    p.CurrentFileIndex + 1,
                    p.TotalFiles);
            });

            var result = await _importService.ImportFolderAsync(request, progress);

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportSuccess) ?? "Imported: {0}",
                $"{result.SuccessCount} / {result.TotalFiles}");

            await LoadTreeAsync();
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

    [RelayCommand(CanExecute = nameof(CanImportToCategoryWithAccess))]
    private async Task ImportFileToCategoryAsync()
    {
        if (SelectedTreeNode is not CategoryNodeViewModel categoryNode) return;
        if (categoryNode.CategoryId == "__no_category__") return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFile) ?? "Import File";
        var paths = _dialogService.ShowImportFilesDialog(title);
        if (paths is null || paths.Length == 0) return;

        IsLoading = true;
        try
        {
            var successCount = 0;
            var skipCount = 0;
            var errorCount = 0;

            for (var i = 0; i < paths.Length; i++)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    i + 1, paths.Length);

                var request = new FamilyImportRequest(
                    paths[i], CurrentRevitVersion, null, null, null, categoryNode.CategoryId);
                var result = await _importService.ImportFileAsync(request);

                if (result.WasSkippedAsDuplicate) skipCount++;
                else if (result.Success) successCount++;
                else errorCount++;
            }

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportSuccess) ?? "Imported: {0}",
                $"{successCount} / {paths.Length}");

            await LoadTreeAsync();
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

    [RelayCommand(CanExecute = nameof(CanImportToCategoryWithAccess))]
    private async Task ImportFolderToCategoryAsync()
    {
        if (SelectedTreeNode is not CategoryNodeViewModel categoryNode) return;
        if (categoryNode.CategoryId == "__no_category__") return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFolder) ?? "Import Folder";
        var path = _dialogService.ShowFolderBrowserDialog(title);
        if (string.IsNullOrWhiteSpace(path)) return;

        IsLoading = true;
        try
        {
            var request = new FamilyFolderImportRequest(
                path!, CurrentRevitVersion, true, null, null, null, categoryNode.CategoryId);
            var progress = new Progress<FamilyImportProgress>(p =>
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    p.CurrentFileIndex + 1,
                    p.TotalFiles);
            });

            var result = await _importService.ImportFolderAsync(request, progress);

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportSuccess) ?? "Imported: {0}",
                $"{result.SuccessCount} / {result.TotalFiles}");

            await LoadTreeAsync();
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

        IsLoading = true;
        StatusMessage = string.Empty;

        FireAndForget(async () =>
        {
            var preparedItems = new List<(string CatalogItemId, string Name, string? FilePath, IReadOnlyList<string> ParamNames, string? VersionId)>();
            var targetRevit = CurrentRevitVersion;

            for (var i = 0; i < families.Count; i++)
            {
                var family = families[i];
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportDataProgress) ?? "Preparing {0} of {1}: {2}",
                    i + 1, families.Count, family.DisplayName);

                var prepareResult = await _dataImportService.PrepareExtractionAsync(family.CatalogItemId, targetRevit, CancellationToken.None);
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

            _externalEvent.Raise(() =>
            {
                try
                {
                    SmartConLogger.FreezeThreadPool("ImportDataForCategory.ExternalEvent.Start");
                    var successCount = 0;
                    var errorCount = 0;

                    for (var i = 0; i < preparedItems.Count; i++)
                    {
                        var item = preparedItems[i];
                        try
                        {
                            StatusMessage = string.Format(
                                LanguageManager.GetString(StringLocalization.Keys.FM_ImportDataProgress) ?? "Processing {0} of {1}: {2}",
                                i + 1, preparedItems.Count, item.Name);

                            var extractionResult = SmartConLogger.FreezeTimer($"ImportDataForCategory.Extract[{item.Name}]", () =>
                                _extractionService.Extract(item.FilePath!, item.ParamNames));

                            SmartConLogger.FreezeTimer($"ImportDataForCategory.SaveResult[{item.Name}]", () =>
                                Task.Run(() => _dataImportService.SaveExtractionResultAsync(
                                    item.CatalogItemId, extractionResult, item.VersionId, null, CancellationToken.None))
                                    .GetAwaiter().GetResult());

                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            errorCount++;
                            SmartConLogger.Freeze($"ImportDataForCategory: Failed for {item.Name} - {ex.Message}");
                            SmartConLogger.Warn($"ImportData failed for {item.Name}: {ex.Message}");
                        }
                    }

                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportResultFormat) ?? "Imported: {0} types, {1} values found",
                        $"{successCount}/{preparedItems.Count} families", "see log");

                    SmartConLogger.Freeze($"ImportDataForCategory: Completed {successCount}/{preparedItems.Count}");

                    FireAndForget(async () => await LoadTreeAsync());
                }
                catch (Exception ex)
                {
                    SmartConLogger.Freeze($"ImportDataForCategory: Exception - {ex.GetType().Name}: {ex.Message}");
                    SmartConLogger.Warn($"ImportDataForCategory failed: {ex.Message}");
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                        ex.Message);
                }
                finally
                {
                    IsLoading = false;
                }
            });
        });
    }

    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private void ExtractTypes()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.Raise(() =>
        {
            try
            {
                SmartConLogger.FreezeThreadPool("ExtractTypes.Start");

                if (_familySearchService.IsFamilyLoaded(selectedName))
                {
                    var names = _familySearchService.GetFamilyTypeNames(selectedName);
                    SmartConLogger.Freeze($"ExtractTypes: Family already loaded, found {names.Count} types");
                    if (names.Count > 0)
                        FireAndForget(() => SaveTypesAndReloadTreeAsync(selectedId, names.ToList()));
                    return;
                }

                var resolved = SmartConLogger.FreezeTimer("ExtractTypes.ResolveFile", () =>
                    Task.Run(() => _fileResolver.ResolveForLoadAsync(selectedId, targetRevit, CancellationToken.None)).GetAwaiter().GetResult());

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    SmartConLogger.Freeze("ExtractTypes: No file resolved");
                    return;
                }

                var typeNames = SmartConLogger.FreezeTimer("ExtractTypes.ExtractFromFile", () =>
                    _familyTypeExtractor.ExtractTypeNamesFromFile(resolved.AbsolutePath));

                SmartConLogger.Freeze($"ExtractTypes: Extracted {typeNames.Count} types");

                if (typeNames.Count > 0)
                    FireAndForget(() => SaveTypesAndReloadTreeAsync(selectedId, typeNames.ToList()));
            }
            catch (Exception ex)
            {
                SmartConLogger.Freeze($"ExtractTypes: Exception - {ex.GetType().Name}: {ex.Message}");
                SmartConLogger.Warn($"ExtractTypes failed: {ex.Message}");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private void ImportData()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var targetRevit = CurrentRevitVersion;

        IsLoading = true;
        StatusMessage = string.Empty;

        FireAndForget(async () =>
        {
            var prepareResult = await _dataImportService.PrepareExtractionAsync(selectedId, targetRevit, CancellationToken.None);

            if (!prepareResult.Success)
            {
                StatusMessage = prepareResult.ErrorMessage ?? LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Preparation error";
                IsLoading = false;
                return;
            }

            if (string.IsNullOrEmpty(prepareResult.ResolvedFilePath))
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "Family file not found";
                IsLoading = false;
                return;
            }

            var rfaPath = prepareResult.ResolvedFilePath;
            var paramNames = prepareResult.ParameterNames;
            var versionId = prepareResult.Item?.CurrentVersionLabel;

            _externalEvent.Raise(() =>
            {
                try
                {
                    SmartConLogger.FreezeThreadPool("ImportData.ExternalEvent.Start");

                    var extractionResult = SmartConLogger.FreezeTimer("ImportData.Extract", () =>
                        _extractionService.Extract(rfaPath!, paramNames));

                    var saveResult = SmartConLogger.FreezeTimer("ImportData.SaveResult", () =>
                        Task.Run(() => _dataImportService.SaveExtractionResultAsync(
                            selectedId, extractionResult, versionId, null, CancellationToken.None)).GetAwaiter().GetResult());

                    StatusMessage = saveResult.Success
                        ? string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportResultFormat) ?? "Imported: {0} types, {1} values found", saveResult.TypesCount, saveResult.AttributesFoundCount)
                        : string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportDataError) ?? "Import error: {0}", saveResult.ErrorMessage);

                    SmartConLogger.Freeze($"ImportData: Success={saveResult.Success}, Types={saveResult.TypesCount}");

                    FireAndForget(async () => await LoadTreeAsync());
                }
                catch (Exception ex)
                {
                    SmartConLogger.Freeze($"ImportData: Exception - {ex.GetType().Name}: {ex.Message}");
                    SmartConLogger.Warn($"ImportData failed: {ex.Message}");
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                        ex.Message);
                }
                finally
                {
                    IsLoading = false;
                }
            });
        });
    }

    private async Task SaveTypesAndReloadTreeAsync(string catalogItemId, List<string> typeNames)
    {
        var types = typeNames.Select((name, i) => new FamilyTypeDescriptor(
            Guid.NewGuid().ToString(), catalogItemId, name, i)).ToList();

        await _typeRepository.SaveTypesAsync(catalogItemId, types.AsReadOnly(), CancellationToken.None);
        await LoadTreeAsync();
    }

    private bool CanImportFiles() => CanImport;

    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private void ImportSystemFamily()
    {
        IsLoading = true;
        StatusMessage = "Select pipe elements in Revit...";

        _externalEvent.Raise(() =>
        {
            try
            {
                SmartConLogger.FreezeThreadPool("ImportSystemFamily.Start");

                var result = _systemFamilyImportService.ImportFromSelection();

                StatusMessage = result.Success
                    ? string.Format("System family imported: {0} types", result.TypesCount)
                    : result.Message ?? "Import failed";

                SmartConLogger.Freeze($"ImportSystemFamily: {result.Message}, ItemId={result.CatalogItemId}");

                if (result.Success)
                    FireAndForget(async () => await LoadTreeAsync());
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                    ex.Message);
                SmartConLogger.Freeze($"ImportSystemFamily: Exception - {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        });
    }

    private bool CanImportToCategoryWithAccess() => CanImport && CanImportToCategory();
}
