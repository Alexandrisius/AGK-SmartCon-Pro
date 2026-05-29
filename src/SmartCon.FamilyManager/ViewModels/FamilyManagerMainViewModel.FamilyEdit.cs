using System.Collections.ObjectModel;
using System.IO;
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
    private async Task UpdateFamilyAsync()
    {
        if (SelectedTreeNode is not FamilyLeafNodeViewModel leaf) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_Update) ?? "Update";
        var path = _dialogService.ShowOpenFileDialog(title);
        if (path is null) return;

        IsLoading = true;
        try
        {
            var request = new FamilyUpdateRequest(leaf.CatalogItemId, path, CurrentRevitVersion);
            var result = await _importService.UpdateFamilyAsync(request);

            if (result.WasSkippedAsDuplicate)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_UpdateIdentical) ?? "File is identical to current version: {0}",
                    result.FileName);
            }
            else if (result.Success)
            {
                // CRITICAL: All UI updates (StatusMessage) MUST be inside ExternalEvent handler
                // to prevent WPF render thread deadlock with MFC family upgrade dialog.
                // See: revit-api-best-practice/references/wpf-mfc-render-freeze.md
                _externalEvent.Raise(() =>
                {
                    try
                    {
                        StatusMessage = string.Format(
                            LanguageManager.GetString(StringLocalization.Keys.FM_UpdateSuccess) ?? "Updated: {0} → {1}",
                            result.FileName,
                            result.VersionLabel);

                        SmartConLogger.Info($"[UpdateFamily] Resolving file path for {leaf.CatalogItemId}...");
                        var resolved = Task.Run(() =>
                            _fileResolver.ResolveForLoadAsync(leaf.CatalogItemId, CurrentRevitVersion, CancellationToken.None))
                            .GetAwaiter().GetResult();

                        if (string.IsNullOrEmpty(resolved.AbsolutePath))
                        {
                            SmartConLogger.Warn($"[UpdateFamily] Empty path resolved for {leaf.CatalogItemId}");
                            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_UpdateError) ?? "Update error";
                            IsLoading = false;
                            return;
                        }

                        SmartConLogger.Info($"[UpdateFamily] Extracting from: {resolved.AbsolutePath}");
                        var extractionResult = _extractionService.Extract(resolved.AbsolutePath, Array.Empty<string>());
                        SmartConLogger.Info($"[UpdateFamily] Extract done: Success={extractionResult.Success}, Types={extractionResult.Types.Count}");

                        if (extractionResult.Success)
                        {
                            var saveResult = Task.Run(() =>
                                _dataImportService.SaveExtractionResultAsync(
                                    leaf.CatalogItemId, extractionResult, result.VersionId, result.FileId, CancellationToken.None))
                                .GetAwaiter().GetResult();

                            StatusMessage = saveResult.Success
                                ? string.Format(
                                    LanguageManager.GetString(StringLocalization.Keys.FM_UpdateSuccess) ?? "Updated: {0}",
                                    result.FileName)
                                : string.Format(
                                    LanguageManager.GetString(StringLocalization.Keys.FM_UpdateError) ?? "Update error: {0}",
                                    saveResult.ErrorMessage);
                        }
                        else
                        {
                            StatusMessage = string.Format(
                                LanguageManager.GetString(StringLocalization.Keys.FM_UpdateError) ?? "Update error: {0}",
                                extractionResult.ErrorMessage ?? "Extraction failed");
                        }

                        IsLoading = false;
                        FireAndForget(() => LoadTreeAsync());
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Warn($"[UpdateFamily] Failed: {ex.Message}");
                        StatusMessage = string.Format(
                            LanguageManager.GetString(StringLocalization.Keys.FM_UpdateError) ?? "Update error: {0}",
                            ex.Message);
                        IsLoading = false;
                    }
                });
            }
            else
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_UpdateError) ?? "Update error: {0}",
                    result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_UpdateError) ?? "Update error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
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
