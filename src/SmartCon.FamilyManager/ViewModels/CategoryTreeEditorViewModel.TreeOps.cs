using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryTreeEditorViewModel
{
    [RelayCommand]
    private async Task AddRoot()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AddCategory) ?? "Add Category";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_CategoryName) ?? "Category name:";
        var name = _dialogService.ShowInputDialog(title, prompt);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var created = await _categoryRepository.AddAsync(name!, null, RootNodes.Count);
            var vm = new CategoryNodeViewModel(created);
            RootNodes.Add(vm);
            SelectedNode = vm;
            vm.IsSelected = true;
            _metadataMediator.RaiseMetadataChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"AddRoot category '{name}' failed: {ex.Message}");
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddChild()
    {
        if (SelectedNode is not CategoryNodeViewModel parent) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AddSubcategory) ?? "Add Subcategory";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_SubcategoryName) ?? "Subcategory name:";
        var name = _dialogService.ShowInputDialog(title, prompt);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var created = await _categoryRepository.AddAsync(name!, parent.CategoryId, parent.Children.Count);
            var childVm = new CategoryNodeViewModel(created);
            parent.Children.Add(childVm);
            parent.IsExpanded = true;
            SelectedNode = childVm;
            childVm.IsSelected = true;
            _metadataMediator.RaiseMetadataChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"AddChild category '{name}' failed: {ex.Message}");
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task Rename()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Rename) ?? "Rename Category";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_NewName) ?? "New name:";
        var newName = _dialogService.ShowInputDialog(title, prompt, node.DisplayName);
        if (string.IsNullOrWhiteSpace(newName) || newName == node.DisplayName) return;

        try
        {
            await _categoryRepository.RenameAsync(node.CategoryId, newName!);
            // Reload: descendant FullPath values change with the name.
            var reselectId = node.CategoryId;
            await LoadTreeAsync();
            var found = FindNodeById(RootNodes, reselectId);
            if (found is not null) found.IsSelected = true;
            SelectedNode = found;
            _metadataMediator.RaiseMetadataChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Rename category '{node.DisplayName}' failed: {ex.Message}");
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;

        var count = node.FamilyCount;
        var noCat = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
        var message = count > 0
            ? string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteConfirm) ?? "Delete \"{0}\"? {1} families will be moved to \"{2}\".", node.DisplayName, count, noCat)
            : string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteEmpty) ?? "Delete \"{0}\"?", node.DisplayName);

        var delTitle = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteCategory) ?? "Delete Category";
        if (!_dialogService.ShowConfirmation(delTitle, message)) return;

        // Find neighbor (next sibling, or previous sibling, or parent) BEFORE removing the node
        // so the user can keep working with a valid selection (right-click context menu otherwise
        // sees SelectedNode=null and skips the confirmation dialog).
        var neighbor = FindNeighborNode(node);

        try
        {
            await _categoryRepository.DeleteAsync(node.CategoryId);
            RemoveNodeFromTree(node.CategoryId);
            if (neighbor is not null) neighbor.IsSelected = true;
            SelectedNode = neighbor;
            _metadataMediator.RaiseMetadataChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Delete category '{node.DisplayName}' failed: {ex.Message}");
            StatusMessage = ex.Message;
        }
    }

    private CategoryNodeViewModel? FindNeighborNode(CategoryNodeViewModel node)
    {
        // Try root level
        for (var i = 0; i < RootNodes.Count; i++)
        {
            if (RootNodes[i].CategoryId == node.CategoryId)
            {
                if (i + 1 < RootNodes.Count) return RootNodes[i + 1];
                if (i > 0) return RootNodes[i - 1];
                return null;
            }
        }

        // Try child level
        return FindNeighborInChildren(RootNodes, node);
    }

    private static CategoryNodeViewModel? FindNeighborInChildren(
        IEnumerable<CategoryNodeViewModel> siblings,
        CategoryNodeViewModel target)
    {
        foreach (var sibling in siblings)
        {
            for (var i = 0; i < sibling.Children.Count; i++)
            {
                if (sibling.Children[i] is CategoryNodeViewModel cat && cat.CategoryId == target.CategoryId)
                {
                    if (i + 1 < sibling.Children.Count) return sibling.Children[i + 1] as CategoryNodeViewModel;
                    if (i > 0) return sibling.Children[i - 1] as CategoryNodeViewModel;
                    return sibling;
                }
            }

            var nested = FindNeighborInChildren(sibling.Children.OfType<CategoryNodeViewModel>(), target);
            if (nested is not null) return nested;
        }

        return null;
    }

    private void RemoveNodeFromTree(string categoryId)
    {
        for (var i = 0; i < RootNodes.Count; i++)
        {
            if (RootNodes[i].CategoryId == categoryId)
            {
                RootNodes.RemoveAt(i);
                return;
            }
            if (TryRemoveNode(RootNodes[i].Children, categoryId))
                return;
        }
    }

    internal static bool TryRemoveNode(ObservableCollection<CatalogTreeNodeViewModel> nodes, string categoryId)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is CategoryNodeViewModel cat && cat.CategoryId == categoryId)
            {
                nodes.RemoveAt(i);
                return true;
            }
            if (TryRemoveNode(nodes[i].Children, categoryId))
                return true;
        }
        return false;
    }

    [RelayCommand]
    private async Task MoveNode((CategoryNodeViewModel Node, CategoryNodeViewModel? NewParent, int SortOrder) args)
    {
        var (node, newParent, sortOrder) = args;
        try
        {
            await _categoryRepository.MoveAsync(node.CategoryId, newParent?.CategoryId, sortOrder);
            var reselectId = node.CategoryId;
            await LoadTreeAsync();
            var found = FindNodeById(RootNodes, reselectId);
            if (found is not null) found.IsSelected = true;
            SelectedNode = found;
            _metadataMediator.RaiseMetadataChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"MoveNode category '{node.DisplayName}' failed: {ex.Message}");
            StatusMessage = ex.Message;
        }
    }

    internal static List<CategoryTreeImportData.CategoryImportItem> BuildExportTree(CategoryTree tree, string? parentId)
    {
        var result = new List<CategoryTreeImportData.CategoryImportItem>();
        foreach (var node in tree.GetChildren(parentId))
        {
            result.Add(new CategoryTreeImportData.CategoryImportItem
            {
                Name = node.Name,
                Children = BuildExportTree(tree, node.Id)
            });
        }
        return result;
    }

    internal static List<CategoryNode> FlattenImportData(List<CategoryTreeImportData.CategoryImportItem> items, string? parentId, int startOrder)
    {
        var result = new List<CategoryNode>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var id = Guid.NewGuid().ToString();
            var path = parentId is null ? item.Name : $"{parentId}/{item.Name}";
            result.Add(new CategoryNode(id, item.Name, parentId, startOrder + i, path, DateTimeOffset.UtcNow));
            if (item.Children?.Count > 0)
            {
                result.AddRange(FlattenImportData(item.Children, id, 0));
            }
        }
        return result;
    }

    private string BuildCategoryPath(CategoryNodeViewModel target)
    {
        var path = new List<string>();
        foreach (var root in RootNodes)
        {
            path.Add(root.DisplayName);
            if (root.CategoryId == target.CategoryId) break;
            if (SearchPathInChildren(root.Children, target.CategoryId, path)) break;
            path.RemoveAt(path.Count - 1);
        }
        return string.Join(" > ", path);
    }

    private static bool SearchPathInChildren(IList<CatalogTreeNodeViewModel> children, string targetId, List<string> path)
    {
        foreach (var child in children)
        {
            if (child is not CategoryNodeViewModel cat) continue;
            path.Add(cat.DisplayName);
            if (cat.CategoryId == targetId) return true;
            if (SearchPathInChildren(cat.Children, targetId, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    private static CategoryNodeViewModel? FindNodeById(ObservableCollection<CategoryNodeViewModel> nodes, string categoryId)
    {
        foreach (var node in nodes)
        {
            if (node.CategoryId == categoryId) return node;
            var found = FindNodeById(node.Children, categoryId);
            if (found is not null) return found;
        }
        return null;
    }

    private static CategoryNodeViewModel? FindNodeById(ObservableCollection<CatalogTreeNodeViewModel> nodes, string categoryId)
    {
        foreach (var node in nodes)
        {
            if (node is CategoryNodeViewModel cat && cat.CategoryId == categoryId) return cat;
            var found = FindNodeById(node.Children, categoryId);
            if (found is not null) return found;
        }
        return null;
    }

    [RelayCommand]
    private void OnSelectedItemChanged(object? selectedItem)
    {
        SelectedNode = selectedItem as CategoryNodeViewModel;
    }

    [RelayCommand]
    private async Task ContextMenuRename()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;
        await Rename();
    }

    [RelayCommand]
    private async Task ContextMenuAddChild()
    {
        if (SelectedNode is not CategoryNodeViewModel parent) return;
        await AddChild();
    }

    [RelayCommand]
    private async Task ContextMenuDelete()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;
        await Delete();
    }

    [RelayCommand]
    private async Task ContextMenuAssignmentRules()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;
        await OpenAssignmentRulesEditorAsync();
    }

    internal async Task OpenAssignmentRulesEditorAsync()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;

        try
        {
            var vm = _viewModelFactory.CreateAssignmentRulesEditorViewModel(node.CategoryId, node.DisplayName);
            await vm.InitializeAsync();
            var saved = _dialogService.ShowAssignmentRulesEditor(vm);
            if (saved == true)
            {
                SmartConLogger.Info($"Assignment rules saved for category '{node.DisplayName}'");
                _metadataMediator.RaiseMetadataChanged();
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Open assignment rules editor for '{node.DisplayName}' failed: {ex.Message}");
            StatusMessage = ex.Message;
        }
    }
}
