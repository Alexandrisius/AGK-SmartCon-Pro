using System.Collections.ObjectModel;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;
using SmartCon.UI.Behaviors;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task OpenCategoryEditorAsync()
    {
        var editorVm = _viewModelFactory.CreateCategoryTreeEditorViewModel();
        editorVm.Saved += () => _ = LoadTreeAsync();
        await editorVm.InitializeAsync();
        _dialogService.ShowCategoryTreeEditor(editorVm);
    }

    [RelayCommand]
    private async Task OpenProperties()
    {
        if (SelectedItem is null) return;

        var itemId = SelectedItem.Id;
        var updatedAt = SelectedItem.UpdatedAtUtc != default
            ? SelectedItem.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : null;

        var vm = _viewModelFactory.CreatePropertiesViewModel(
            SelectedItem.Id,
            SelectedItem.Name,
            SelectedItem.Description,
            SelectedItem.CategoryId,
            SelectedItem.CategoryName,
            SelectedItem.Tags,
            SelectedItem.ContentStatus,
            SelectedItem.Manufacturer,
            SelectedItem.VersionLabel,
            null,
            null,
            updatedAt,
            isReadOnly: !CanEdit);

        vm.InitializeCommand.Execute(null);
        var result = _dialogService.ShowProperties(vm);
        if (result != true) return;

        await LoadTreeAsync();
        ExpandAndSelectItem(itemId);
    }

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task EditFamilyAsync()
    {
        if (SelectedTreeNode is not FamilyLeafNodeViewModel leaf) return;

        var resolved = await _fileResolver.ResolveForLoadAsync(
            leaf.CatalogItemId, CurrentRevitVersion, CancellationToken.None);
        if (string.IsNullOrEmpty(resolved.AbsolutePath))
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "Family file not found in managed storage.");
            return;
        }

        _externalEvent.RaiseWithApplication(obj =>
        {
            try
            {
                var app = (Autodesk.Revit.UI.UIApplication)obj;
                app.OpenAndActivateDocument(resolved.AbsolutePath);
            }
            catch (Exception ex)
            {
                SmartConLogger.Error($"EditFamily OpenAndActivateDocument failed: {ex.Message}");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task ImportActiveFileAsync()
    {
        IsLoading = true;
        string? familyPath = null;
        try
        {
            SmartConLogger.Info("[ImportActiveFile] Started");
            var scan = await ScanActiveFileAsync();
            if (scan is null) return;

            if (scan.Kind == ImportActiveKind.Family)
            {
                familyPath = scan.FamilyPath;
                await ProcessFamilyImportAsync(scan.FamilyPath!);
            }
            else if (scan.Kind == ImportActiveKind.Project)
            {
                await ProcessProjectImportAsync(scan.PendingItems!);
            }
            else
            {
                _dialogService.ShowError(
                    LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "Error",
                    LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound)
                        ?? "В проекте не найдено размещённых системных семейств");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[ImportActiveFile] Failed: {ex}");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error",
                ex.Message);
        }
        finally
        {
            var capturedFamilyPath = familyPath;
            var cleanupTcs = new TaskCompletionSource<bool>();
            _externalEvent.RaiseWithApplication(obj =>
            {
                try
                {
                    var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                    var app = uiApp.Application;

                    if (!string.IsNullOrEmpty(capturedFamilyPath))
                    {
                        var activeBeforeSwitch = uiApp.ActiveUIDocument?.Document?.PathName;
                        var projectDoc = app.Documents.Cast<Document>()
                            .FirstOrDefault(d => !d.IsFamilyDocument && !d.IsLinked
                                && !string.IsNullOrEmpty(d.PathName)
                                && d.PathName != activeBeforeSwitch);

                        SmartConLogger.Info(
                            $"[ImportActiveFile] Cleanup(family): activeBeforeSwitch='{activeBeforeSwitch}', " +
                            $"projectToSwitch='{projectDoc?.PathName}'");

                        if (projectDoc != null)
                        {
                            try
                            {
                                uiApp.OpenAndActivateDocument(projectDoc.PathName);
                            }
                            catch (Exception activateEx)
                            {
                                SmartConLogger.Warn(
                                    $"[ImportActiveFile] Activate project failed: {activateEx.Message}");
                                try
                                {
                                    var closeCmd = RevitCommandId.LookupPostableCommandId(PostableCommand.Close);
                                    uiApp.PostCommand(closeCmd);
                                }
                                catch { }
                            }
                        }
                        else
                        {
                            try
                            {
                                var closeCmd = RevitCommandId.LookupPostableCommandId(PostableCommand.Close);
                                uiApp.PostCommand(closeCmd);
                            }
                            catch (Exception postEx)
                            {
                                SmartConLogger.Warn(
                                    $"[ImportActiveFile] PostCommand Close failed: {postEx.Message}");
                            }
                        }

                        var activeAfterSwitch = uiApp.ActiveUIDocument?.Document?.PathName;
                        if (!string.IsNullOrEmpty(capturedFamilyPath)
                            && !string.Equals(activeAfterSwitch, capturedFamilyPath, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var docToClose = app.Documents.Cast<Document>()
                                    .FirstOrDefault(d => string.Equals(
                                        d.PathName, capturedFamilyPath, StringComparison.OrdinalIgnoreCase));
                                if (docToClose != null && !docToClose.IsLinked)
                                {
                                    docToClose.Close(false);
                                    SmartConLogger.Info(
                                        $"[ImportActiveFile] Closed family file: {capturedFamilyPath}");
                                }
                            }
                            catch (Exception closeEx)
                            {
                                SmartConLogger.Info(
                                    $"[ImportActiveFile] Family close skipped: {closeEx.Message}");
                            }
                        }
                    }

                    CleanupImportActiveTemp();
                    cleanupTcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Error($"[ImportActiveFile] Cleanup failed: {ex}");
                    cleanupTcs.TrySetResult(false);
                }
            });
            try { await cleanupTcs.Task; } catch { }
            IsLoading = false;
        }
    }

    private async Task<ImportActiveScanResult?> ScanActiveFileAsync()
    {
        var tcs = new TaskCompletionSource<ImportActiveScanResult?>();
        _externalEvent.RaiseWithApplication(obj =>
        {
            try
            {
                var app = (Autodesk.Revit.UI.UIApplication)obj;
                var activeDoc = app.ActiveUIDocument?.Document;
                if (activeDoc is null) { tcs.SetResult(null); return; }

                if (activeDoc.IsFamilyDocument)
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "SmartCon", "FMLoad", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);
                    var safeName = Path.GetFileNameWithoutExtension(activeDoc.Title);
                    if (string.IsNullOrWhiteSpace(safeName)) safeName = "Family";
                    foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
                    var tempPath = Path.Combine(tempDir, safeName + ".rfa");
                    activeDoc.SaveAs(tempPath);
                    SmartConLogger.Info(
                        $"[ImportActiveFile] Family doc saved to: {tempPath}");
                    tcs.SetResult(new ImportActiveScanResult(ImportActiveKind.Family, tempPath, null, null));
                }
                else
                {
                    var pending = _systemFamilyImportService.AnalyzeAndPrepareForProject(activeDoc);
                    if (pending.Count == 0)
                    {
                        SmartConLogger.Info("[ImportActiveFile] No system types found in active project");
                        tcs.SetResult(new ImportActiveScanResult(ImportActiveKind.ProjectEmpty, null, null, null));
                        return;
                    }
                    var paths = pending.Select(p => p.TempRvtPath).ToList();
                    SmartConLogger.Info(
                        $"[ImportActiveFile] Project: {pending.Count} categories prepared, {paths.Count} temp .rvt files");
                    tcs.SetResult(new ImportActiveScanResult(ImportActiveKind.Project, null, pending, paths));
                }
            }
            catch (Exception ex) { tcs.SetException(ex); }
        });

        try { return await tcs.Task; }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[ImportActiveFile] Scan failed: {ex.Message}");
            return null;
        }
    }

    private async Task ProcessFamilyImportAsync(string familyRfaPath)
    {
        var metadata = await _metadataService.ExtractAsync(familyRfaPath, CancellationToken.None);
        var revitVersion = _fileInfoReader.ReadRevitVersion(familyRfaPath) ?? CurrentRevitVersion;
        var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(
            Path.GetFileNameWithoutExtension(familyRfaPath));
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None);

        var existingCategoryId = existingByName?.CategoryId;
        var existingCategoryName = existingByName?.CategoryPath;
        if (existingCategoryId is not null && existingCategoryName is null)
        {
            try
            {
                var cat = await _categoryRepository.GetByIdAsync(existingCategoryId, CancellationToken.None);
                existingCategoryName = cat?.Name;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"[ImportActiveFile] Failed to resolve category name: {ex.Message}");
            }
        }

        var item = new FamilyBatchImportItem(
            familyRfaPath,
            Path.GetFileNameWithoutExtension(familyRfaPath),
            metadata.Sha256,
            revitVersion,
            new FileInfo(familyRfaPath).Length,
            existingByName is not null ? FamilyBatchImportStatus.Existing : FamilyBatchImportStatus.New,
            existingByName?.Id,
            existingByName?.CurrentVersionLabel,
            existingCategoryId,
            existingCategoryName);

        using var vm = new FamilyBatchImportViewModel(new[] { item }, _dialogService, _viewModelFactory, _catalogProvider);
        if (_dialogService.ShowBatchImportDialog(vm) != true) return;

        var selectedItems = vm.GetResultItems();
        var progress = new Progress<FamilyImportProgress>(p =>
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                p.CurrentFileIndex + 1, p.TotalFiles);
        });

        var importResult = await _importService.ImportBatchAsync(selectedItems, null, progress, CancellationToken.None);

        var successfulItems = importResult.Results
            .Where(r => r.Success && !r.WasSkippedAsDuplicate).ToList();

        if (successfulItems.Count > 0) await LoadTreeAsync();

        ExtractAttributesForImportedFamilies(importResult.Results);

        StatusMessage = BuildImportStatusMessage(
            importResult.SuccessCount, importResult.SkippedCount,
            importResult.ErrorCount, importResult.TotalFiles);
    }

    private async Task ProcessProjectImportAsync(IReadOnlyList<SystemFamilyPendingImport> pendingItems)
    {
        var batchItems = new List<FamilyBatchImportItem>(pendingItems.Count);
        var pendingByFile = new Dictionary<string, SystemFamilyPendingImport>(StringComparer.OrdinalIgnoreCase);

        foreach (var pending in pendingItems)
        {
            pendingByFile[Path.GetFileName(pending.TempRvtPath)] = pending;

            if (!File.Exists(pending.TempRvtPath))
            {
                SmartConLogger.Warn(
                    $"[ImportActiveFile] Temp .rvt missing: {pending.TempRvtPath}");
                continue;
            }

            var metadata = await _metadataService.ExtractAsync(pending.TempRvtPath, CancellationToken.None);
            var revitVersion = _fileInfoReader.ReadRevitVersion(pending.TempRvtPath) ?? CurrentRevitVersion;
            var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(
                Path.GetFileNameWithoutExtension(pending.TempRvtPath));
            var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None);

            var existingCategoryId = existingByName?.CategoryId;
            var existingCategoryName = existingByName?.CategoryPath;
            if (existingCategoryId is not null && existingCategoryName is null)
            {
                try
                {
                    var cat = await _categoryRepository.GetByIdAsync(existingCategoryId, CancellationToken.None);
                    existingCategoryName = cat?.Name;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"[ImportActiveFile] Failed to resolve category for '{pending.CategoryName}': {ex.Message}");
                }
            }

            var displayName = Path.GetFileNameWithoutExtension(pending.TempRvtPath);
            batchItems.Add(new FamilyBatchImportItem(
                pending.TempRvtPath,
                displayName,
                metadata.Sha256,
                revitVersion,
                new FileInfo(pending.TempRvtPath).Length,
                existingByName is not null ? FamilyBatchImportStatus.Existing : FamilyBatchImportStatus.New,
                existingByName?.Id,
                existingByName?.CurrentVersionLabel,
                existingCategoryId,
                existingCategoryName,
                FamilySource: "system",
                TypeCount: pending.Types.Count,
                RevitCategory: pending.CategoryName));
        }

        if (batchItems.Count == 0)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "No system families found");
            return;
        }

        using var vm = new FamilyBatchImportViewModel(batchItems, _dialogService, _viewModelFactory, _catalogProvider);
        if (_dialogService.ShowBatchImportDialog(vm) != true) return;

        var selectedItems = vm.GetResultItems();
        var toImport = selectedItems.Where(i => i.Action != FamilyBatchImportAction.Skip).ToList();
        if (toImport.Count == 0) return;

        StatusMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyPreparing) ?? "Импорт {0} системных семейств...",
            toImport.Count);

        var result = await _systemFamilyImportService.ImportBatchItemsAsync(toImport);

        var totalTypes = pendingItems
            .Where(p => toImport.Any(i => string.Equals(
                Path.GetFileName(i.FilePath), Path.GetFileName(p.TempRvtPath), StringComparison.OrdinalIgnoreCase)))
            .Sum(p => p.Types.Count);

        StatusMessage = result.Success
            ? string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImported) ?? "Импортировано: {0}",
                totalTypes)
            : result.Message ?? "Ошибка импорта";

        if (result.Success) await LoadTreeAsync();

        // Извлечение атрибутов из .rvt (по образцу ImportSystemFamilyCommand: Import.cs:366-422).
        // Revit's ExtractFromRvt требует UI thread → ExternalEvent.
        ExtractAttributesFromRvts(result.ExtractionTasks);
    }

    private void ExtractAttributesFromRvts(IReadOnlyList<SystemFamilyExtractionTask> tasks)
    {
        if (tasks.Count == 0) return;

        _externalEvent.Raise(() =>
        {
            try
            {
                foreach (var task in tasks)
                {
                    try
                    {
                        if (!File.Exists(task.TempRvtPath))
                        {
                            SmartConLogger.Warn(
                                $"[ImportActiveFile] Temp .rvt not found for extraction: {task.TempRvtPath}");
                            continue;
                        }

                        var extraction = _systemFamilyAttributeExtraction.ExtractFromRvt(task.TempRvtPath, task.TypeNames);
                        if (extraction.Success)
                        {
                            FireAndForget(async () =>
                            {
                                try
                                {
                                    await _dataImportService.SaveExtractionResultAsync(
                                        task.CatalogItemId, extraction, task.VersionId, task.FileId, CancellationToken.None);
                                    SmartConLogger.Info(
                                        $"[ImportActiveFile] Saved extraction for '{Path.GetFileName(task.TempRvtPath)}': {extraction.Types.Count} types");
                                }
                                catch (Exception ex)
                                {
                                    SmartConLogger.Warn($"[ImportActiveFile] SaveExtractionResult failed: {ex.Message}");
                                }
                            });
                        }
                        else
                        {
                            SmartConLogger.Warn(
                                $"[ImportActiveFile] Extraction failed for '{Path.GetFileName(task.TempRvtPath)}': {extraction.ErrorMessage}");
                        }
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Warn($"[ImportActiveFile] Extraction exception for '{task.TempRvtPath}': {ex.Message}");
                    }
                }
            }
            finally
            {
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
            }
        });
    }

    /// <summary>
    /// Извлекает атрибуты (Type parameters) из импортированных .rfa-файлов.
    /// Зеркалит поведение старого Import.cs (ExtractTypesForImportedFamilies):
    /// после успешного импорта резолвит файл в managed storage, вызывает
    /// <see cref="IFamilyDataExtractionService.Extract"/> и сохраняет результат
    /// через <see cref="IFamilyDataImportService.SaveExtractionResultAsync"/>.
    /// </summary>
    private void ExtractAttributesForImportedFamilies(IReadOnlyList<FamilyImportResult> importResults)
    {
        var extractionResults = new List<(string CatalogItemId, FamilyExtractionResult Result, string? VersionId, string? FileId, bool HasTypeCatalog)>();

        try
        {
            foreach (var item in importResults)
            {
                if (!item.Success || item.WasSkippedAsDuplicate) continue;
                if (string.IsNullOrEmpty(item.CatalogItemId)) continue;
                var catalogItemId = item.CatalogItemId!;

                var resolved = Task.Run(() =>
                    _fileResolver.ResolveForLoadAsync(catalogItemId, CurrentRevitVersion, CancellationToken.None))
                    .GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(resolved.AbsolutePath)) continue;

                var txtPath = Path.ChangeExtension(resolved.AbsolutePath, ".txt");
                var hasTypeCatalog = File.Exists(txtPath);

                var extraction = _extractionService.Extract(resolved.AbsolutePath, Array.Empty<string>());
                if (extraction.Success)
                {
                    extractionResults.Add((catalogItemId, extraction, item.VersionId, item.FileId, hasTypeCatalog));
                    SmartConLogger.Info(
                        $"[ImportActiveFile] Extracted {extraction.Types.Count} type(s) from '{Path.GetFileName(resolved.AbsolutePath)}'");
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[ImportActiveFile] Attribute extraction failed: {ex.Message}");
        }

        if (extractionResults.Count == 0) return;

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
                SmartConLogger.Warn($"[ImportActiveFile] SaveExtraction failed: {ex.Message}");
            }
        });
    }

    private static void CleanupImportActiveTemp()
    {
        try
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "SmartCon");
            if (!Directory.Exists(tempRoot)) return;

            foreach (var sub in new[] { "FMLoad", "SystemFamilyLoad", "SystemFamilyLoadFromProject" })
            {
                var dir = Path.Combine(tempRoot, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (var childDir in Directory.GetDirectories(dir))
                {
                    try { Directory.Delete(childDir, true); } catch { }
                }
            }
        }
        catch { }
    }

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task EditSystemFamilyAsync()
    {
        if (SelectedTreeNode is not FamilyLeafNodeViewModel leaf) return;
        if (leaf.FamilySource != "system") return;

        var resolved = await _fileResolver.ResolveForLoadAsync(
            leaf.CatalogItemId, CurrentRevitVersion, CancellationToken.None);
        if (string.IsNullOrEmpty(resolved.AbsolutePath))
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "System family file not found in managed storage.");
            return;
        }

        _externalEvent.RaiseWithApplication(obj =>
        {
            try
            {
                var app = (Autodesk.Revit.UI.UIApplication)obj;
                app.OpenAndActivateDocument(resolved.AbsolutePath);
            }
            catch (Exception ex)
            {
                SmartConLogger.Freeze($"[EditSystemFamily] OpenAndActivateDocument failed: {ex.Message}");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task DeleteFamilyAsync()
    {
        if (SelectedItem is null) return;

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteTitle) ?? "Delete Family",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeletePrompt) ?? "Delete \"{0}\"?",
                SelectedItem.Name));

        if (!confirmed) return;

        IsLoading = true;
        try
        {
            var success = await _writableProvider.DeleteItemAsync(SelectedItem.Id);
            if (success)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleted) ?? "Deleted: {0}",
                    SelectedItem.Name);
                await LoadTreeAsync();
            }
        }
        catch (IOException ex)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteError) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteInUse) ?? "Failed to delete family files. The file may be open in Revit or another application. Close the file and try again.");
            StatusMessage = $"{LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteError) ?? "Error"}: {ex.Message}";
            await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"{LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteError) ?? "Error"}: {ex.Message}";
            await LoadTreeAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task MoveFamilyToCategoryAsync(string familyId, string? targetCategoryId)
    {
        if (!CanEdit)
        {
            SmartConLogger.Warn($"[FM] MoveFamilyToCategoryAsync blocked: user lacks edit permissions.");
            return;
        }

        IsLoading = true;
        try
        {
            await _writableProvider.UpdateItemAsync(familyId, null, null, targetCategoryId, null, null);
            await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartDrag))]
    private void StartDrag(object? item)
    {
        // Drag permission gate. Behavior handles the actual DoDragDrop.
    }

    private bool CanStartDrag(object? item) => item is FamilyLeafNodeViewModel && CanEdit;

    [RelayCommand(CanExecute = nameof(CanDropFamily))]
    private async Task DropFamilyAsync(TreeViewDropInfo? info)
    {
        if (info is not { Payload: FamilyLeafNodeViewModel leaf, Target: CategoryNodeViewModel target })
            return;

        var categoryId = target.CategoryId == "__no_category__"
            ? null
            : target.CategoryId;

        await MoveFamilyToCategoryAsync(leaf.CatalogItemId, categoryId);
    }

    private bool CanDropFamily(TreeViewDropInfo? info)
    {
        if (info is null) return false;
        return info.Payload is FamilyLeafNodeViewModel
            && info.Target is CategoryNodeViewModel
            && CanEdit;
    }

    private void ExpandAndSelectItem(string catalogItemId)
    {
        foreach (var root in TreeNodes)
        {
            if (ExpandToItem(root, catalogItemId))
                return;
        }
    }

    private bool ExpandToItem(CatalogTreeNodeViewModel node, string catalogItemId)
    {
        foreach (var child in node.Children)
        {
            if (child is FamilyLeafNodeViewModel leaf && leaf.CatalogItemId == catalogItemId)
            {
                node.IsExpanded = true;
                leaf.IsSelected = true;
                SelectedTreeNode = leaf;
                return true;
            }
            if (ExpandToItem(child, catalogItemId))
            {
                node.IsExpanded = true;
                return true;
            }
        }
        return false;
    }

    private bool CanEditOps() => CanEdit;

    private enum ImportActiveKind
    {
        Family,
        Project,
        ProjectEmpty
    }

    private sealed record ImportActiveScanResult(
        ImportActiveKind Kind,
        string? FamilyPath,
        IReadOnlyList<SystemFamilyPendingImport>? PendingItems,
        IReadOnlyList<string>? RvtPaths);
}
