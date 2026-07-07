using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryTreeEditorViewModel
{
    [RelayCommand]
    private void AddRoot()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AddCategory) ?? "Add Category";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_CategoryName) ?? "Category name:";
        var name = _dialogService.ShowInputDialog(title, prompt);
        if (string.IsNullOrWhiteSpace(name)) return;

        var sortOrder = RootNodes.Count;
        var categoryId = Guid.NewGuid().ToString();
        var vm = new CategoryNodeViewModel(categoryId, name!, null, name!)
        {
            SortOrder = sortOrder,
            OriginalSortOrder = sortOrder,
            IsNew = true,
            IsDirty = true
        };
        RootNodes.Add(vm);
        SelectedNode = vm;
        vm.IsSelected = true;
        UpdateHasUnsavedChanges();
    }

    [RelayCommand]
    private void AddChild()
    {
        if (SelectedNode is not CategoryNodeViewModel parent) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AddSubcategory) ?? "Add Subcategory";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_SubcategoryName) ?? "Subcategory name:";
        var name = _dialogService.ShowInputDialog(title, prompt);
        if (string.IsNullOrWhiteSpace(name)) return;

        var sortOrder = parent.Children.Count;
        var categoryId = Guid.NewGuid().ToString();
        var fullPath = $"{parent.FullPath}/{name}";
        var childVm = new CategoryNodeViewModel(categoryId, name!, parent.CategoryId, fullPath)
        {
            SortOrder = sortOrder,
            OriginalSortOrder = sortOrder,
            IsNew = true,
            IsDirty = true
        };
        parent.Children.Add(childVm);
        parent.IsExpanded = true;
        SelectedNode = childVm;
        childVm.IsSelected = true;
        UpdateHasUnsavedChanges();
    }

    [RelayCommand]
    private void Rename()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Rename) ?? "Rename Category";
        var prompt = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_NewName) ?? "New name:";
        var newName = _dialogService.ShowInputDialog(title, prompt, node.DisplayName);
        if (string.IsNullOrWhiteSpace(newName) || newName == node.DisplayName) return;

        node.DisplayName = newName!;
        node.IsDirty = true;
        UpdateHasUnsavedChanges();
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;

        var count = node.FamilyCount;
        var noCat = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
        var message = count > 0
            ? string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteConfirm) ?? "Delete \"{0}\"? {1} families will be moved to \"{2}\".", node.DisplayName, count, noCat)
            : string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteEmpty) ?? "Delete \"{0}\"?", node.DisplayName);

        var delTitle = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_DeleteCategory) ?? "Delete Category";
        if (!_dialogService.ShowConfirmation(delTitle, message)) return;

        // Remove binding changes for this category and all descendants to prevent FK errors on save
        var allIds = GetCategoryAndDescendantIds(node);
        foreach (var id in allIds)
        {
            var keysToRemove = _bindingChanges.Keys.Where(k => k.StartsWith(id + ":", StringComparison.Ordinal)).ToList();
            foreach (var key in keysToRemove)
                _bindingChanges.Remove(key);
        }

        // Find neighbor (next sibling, or previous sibling, or parent) BEFORE removing the node
        // so the user can keep working with a valid selection (right-click context menu otherwise
        // sees SelectedNode=null and skips the confirmation dialog).
        var neighbor = FindNeighborNode(node);

        node.IsDeleted = true;
        _pendingCategoryDeletions.Add(node);
        RemoveNodeFromTree(node.CategoryId);
        SelectedNode = neighbor;
        UpdateHasUnsavedChanges();
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

    private static List<string> GetCategoryAndDescendantIds(CategoryNodeViewModel node)
    {
        var result = new List<string> { node.CategoryId };
        foreach (var child in node.Children)
        {
            if (child is CategoryNodeViewModel cat)
                result.AddRange(GetCategoryAndDescendantIds(cat));
        }
        return result;
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
    private void MoveNode((CategoryNodeViewModel Node, CategoryNodeViewModel? NewParent, int SortOrder) args)
    {
        var (node, newParent, sortOrder) = args;
        node.ParentId = newParent?.CategoryId;
        node.SortOrder = sortOrder;
        node.IsDirty = true;
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

    [RelayCommand]
    private void OnSelectedItemChanged(object? selectedItem)
    {
        SelectedNode = selectedItem as CategoryNodeViewModel;
    }

    [RelayCommand]
    private void ContextMenuRename()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;
        Rename();
    }

    [RelayCommand]
    private void ContextMenuAddChild()
    {
        if (SelectedNode is not CategoryNodeViewModel parent) return;
        AddChild();
    }

    [RelayCommand]
    private void ContextMenuDelete()
    {
        if (SelectedNode is not CategoryNodeViewModel node) return;
        Delete();
    }
}
