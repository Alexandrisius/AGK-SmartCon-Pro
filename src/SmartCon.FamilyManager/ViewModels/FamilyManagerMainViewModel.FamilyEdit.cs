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
    private async Task LoadActiveFamilyAsync()
    {
        IsLoading = true;
        try
        {
            var tcs = new TaskCompletionSource<string?>();
            _externalEvent.RaiseWithApplication(obj =>
            {
                try
                {
                    var app = (Autodesk.Revit.UI.UIApplication)obj;
                    var activeDoc = app.ActiveUIDocument.Document;
                    if (!activeDoc.IsFamilyDocument)
                    {
                        tcs.SetResult(null);
                        return;
                    }
                    var tempDir = Path.Combine(Path.GetTempPath(), "SmartCon", "FMLoad", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);
                    var tempPath = Path.Combine(tempDir, activeDoc.Title + ".rfa");
                    activeDoc.SaveAs(tempPath);
                    tcs.SetResult(tempPath);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            var familyPath = await tcs.Task;
            if (familyPath is null)
            {
                _dialogService.ShowError(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ActiveDocNotFamily) ?? "Error",
                    LanguageManager.GetString(StringLocalization.Keys.FM_ActiveDocNotFamily) ?? "Активный документ не является семейством");
                return;
            }

            var metadata = await _metadataService.ExtractAsync(familyPath, CancellationToken.None);
            var revitVersion = _fileInfoReader.ReadRevitVersion(familyPath) ?? CurrentRevitVersion;
            var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(Path.GetFileNameWithoutExtension(familyPath));
            var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None);

            // Resolve category name if missing (category_name can be NULL while category_id is set)
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
                    SmartConLogger.Warn($"[LoadActiveFamily] Failed to resolve category name: {ex.Message}");
                }
            }

            var item = new FamilyBatchImportItem(
                familyPath,
                Path.GetFileName(familyPath),
                metadata.Sha256,
                revitVersion,
                new FileInfo(familyPath).Length,
                existingByName is not null ? FamilyBatchImportStatus.Existing : FamilyBatchImportStatus.New,
                existingByName?.Id,
                existingByName?.CurrentVersionLabel,
                existingCategoryId,
                existingCategoryName);

            using var vm = new FamilyBatchImportViewModel(new[] { item }, _dialogService, _viewModelFactory);
            if (_dialogService.ShowBatchImportDialog(vm) != true)
            {
                return;
            }

            var selectedItems = vm.GetResultItems();
            var progress = new Progress<FamilyImportProgress>(p =>
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    p.CurrentFileIndex + 1, p.TotalFiles);
            });

            var importResult = await _importService.ImportBatchAsync(
                selectedItems, null, progress, CancellationToken.None);

            var successfulItems = importResult.Results
                .Where(r => r.Success && !r.WasSkippedAsDuplicate).ToList();

            var itemsWithTypeCatalog = new List<FamilyImportResult>();
            var itemsWithoutTypeCatalog = new List<FamilyImportResult>();
            foreach (var si in successfulItems)
            {
                if (string.IsNullOrEmpty(si.CatalogItemId) || string.IsNullOrEmpty(si.VersionLabel))
                {
                    itemsWithoutTypeCatalog.Add(si);
                    continue;
                }

                var resolved = await _fileResolver.ResolveVersionAsync(si.CatalogItemId!, si.VersionLabel!, CancellationToken.None);
                if (!string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    var txtPath = Path.ChangeExtension(resolved.AbsolutePath, ".txt");
                    if (File.Exists(txtPath))
                    {
                        SmartConLogger.Info($"[LoadActiveFamily] Type Catalog found for {si.CatalogItemId} {si.VersionLabel}");
                        itemsWithTypeCatalog.Add(si);
                        continue;
                    }
                }
                itemsWithoutTypeCatalog.Add(si);
            }

            var extractionTcs = new TaskCompletionSource<FamilyExtractionResult?>();
            _externalEvent.RaiseWithApplication(obj =>
            {
                try
                {
                    var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                    var app = uiApp.Application;
                    var activeDoc = uiApp.ActiveUIDocument?.Document;
                    if (activeDoc == null || !activeDoc.IsFamilyDocument)
                    {
                        extractionTcs.SetResult(null);
                        return;
                    }

                    string? familyPath = activeDoc.PathName;

                    if (successfulItems.Count > 0)
                    {
                        var extractionResult = _extractionService.Extract(activeDoc, Array.Empty<string>());
                        extractionTcs.SetResult(extractionResult);
                    }
                    else
                    {
                        extractionTcs.SetResult(null);
                    }

                    // Switch to project first (cannot close active document directly)
                    var projectDoc = app.Documents.Cast<Document>()
                        .FirstOrDefault(d => !d.IsFamilyDocument && !d.IsLinked);

                    if (projectDoc != null && !string.IsNullOrEmpty(projectDoc.PathName))
                    {
                        try
                        {
                            uiApp.OpenAndActivateDocument(projectDoc.PathName);
                        }
                        catch (Exception activateEx)
                        {
                            SmartConLogger.Warn($"[LoadActiveFamily] Failed to activate project: {activateEx.Message}");
                            // Fallback: close via PostCommand since we can't close active doc directly
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
                        // No project document open — use PostCommand to close active family via UI
                        try
                        {
                            var closeCmd = RevitCommandId.LookupPostableCommandId(PostableCommand.Close);
                            uiApp.PostCommand(closeCmd);
                        }
                        catch (Exception postEx)
                        {
                            SmartConLogger.Warn($"[LoadActiveFamily] PostCommand Close failed: {postEx.Message}");
                        }
                    }

                    // Close the family document only if it is no longer active
                    var currentActivePath = uiApp.ActiveUIDocument?.Document?.PathName;
                    if (!string.IsNullOrEmpty(familyPath) && currentActivePath != familyPath)
                    {
                        var familyDoc = app.Documents.Cast<Document>()
                            .FirstOrDefault(d => d.IsFamilyDocument && d.PathName == familyPath);
                        if (familyDoc != null)
                        {
                            try
                            {
                                familyDoc.Close(false);
                                SmartConLogger.Info($"[LoadActiveFamily] Closed family document: {familyDoc.Title}");
                            }
                            catch (Exception closeEx)
                            {
                                SmartConLogger.Warn($"[LoadActiveFamily] Failed to close family document: {closeEx.Message}");
                            }
                        }
                    }

                    CleanupFamilyManagerTemp();
                }
                catch (Exception ex)
                {
                    SmartConLogger.Error($"LoadActiveFamily cleanup failed: {ex}");
                    extractionTcs.TrySetResult(null);
                }
            });

            var extractionResult = await extractionTcs.Task;
            if (extractionResult != null)
            {
                foreach (var si in itemsWithoutTypeCatalog)
                {
                    if (string.IsNullOrEmpty(si.CatalogItemId)) continue;
                    await _dataImportService.SaveExtractionResultAsync(
                        si.CatalogItemId!, extractionResult, si.VersionId, si.FileId, CancellationToken.None);
                }

                foreach (var si in itemsWithTypeCatalog)
                {
                    if (string.IsNullOrEmpty(si.CatalogItemId)) continue;
                    await _dataImportService.MergeMissingValuesAsync(
                        si.CatalogItemId!, extractionResult, si.VersionId, si.FileId, CancellationToken.None);
                }
            }
            
            // Always reload tree if there were successful imports (even if extraction was skipped)
            if (successfulItems.Count > 0)
            {
                await LoadTreeAsync();
            }

            StatusMessage = BuildImportStatusMessage(
                importResult.SuccessCount, importResult.SkippedCount,
                importResult.ErrorCount, importResult.TotalFiles);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void CleanupFamilyManagerTemp()
    {
        try
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "SmartCon");
            if (!Directory.Exists(tempRoot)) return;

            var dir = Path.Combine(tempRoot, "FMLoad");
            if (!Directory.Exists(dir)) return;
            foreach (var childDir in Directory.GetDirectories(dir))
            {
                try { Directory.Delete(childDir, true); } catch { }
            }
        }
        catch { }
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
}
