using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    /// <summary>
    /// Recursively expands the subtree rooted at <paramref name="node"/>.
    /// Sets <see cref="CatalogTreeNodeViewModel.IsExpanded"/> to <c>true</c>
    /// on every <see cref="CategoryNodeViewModel"/> in the subtree. Leaves
    /// (<see cref="FamilyLeafNodeViewModel"/>) are ignored — their IsExpanded
    /// has no visual effect.
    /// </summary>
    internal static void ExpandSubtree(CatalogTreeNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            if (child is CategoryNodeViewModel catChild)
                ExpandSubtree(catChild);
        }

        if (node is CategoryNodeViewModel cat)
            cat.IsExpanded = true;
    }

    /// <summary>
    /// Recursively collapses the subtree rooted at <paramref name="node"/>,
    /// INCLUDING the root node itself. Matches VS Solution Explorer behavior:
    /// "Collapse All" closes the whole subtree, not just the children.
    /// </summary>
    internal static void CollapseSubtree(CatalogTreeNodeViewModel node)
    {
        if (node is CategoryNodeViewModel cat)
            cat.IsExpanded = false;

        foreach (var child in node.Children)
        {
            if (child is CategoryNodeViewModel catChild)
                CollapseSubtree(catChild);
        }
    }

    /// <summary>
    /// Expands every root category and all of its descendants.
    /// </summary>
    internal static void ExpandAll(IEnumerable<CatalogTreeNodeViewModel> roots)
    {
        foreach (var root in roots)
            ExpandSubtree(root);
    }

    /// <summary>
    /// Collapses every root category and all of its descendants (including roots).
    /// </summary>
    internal static void CollapseAll(IEnumerable<CatalogTreeNodeViewModel> roots)
    {
        foreach (var root in roots)
            CollapseSubtree(root);
    }

    /// <summary>
    /// Returns <c>true</c> if any category in the subtree rooted at
    /// <paramref name="category"/> is collapsed (including the root itself).
    /// Used by the hover-reveal toggle button to decide whether clicking
    /// should expand or collapse.
    /// </summary>
    internal static bool IsAnyDescendantCollapsed(CategoryNodeViewModel category)
    {
        if (!category.IsExpanded)
            return true;

        foreach (var child in category.Children)
        {
            if (child is CategoryNodeViewModel catChild && IsAnyDescendantCollapsed(catChild))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Expands the entire tree. Bound to the "Развернуть всё" button in the status bar.
    /// </summary>
    [RelayCommand]
    private void ExpandAllTree()
    {
        ExpandAll(TreeNodes);
    }

    /// <summary>
    /// Collapses the entire tree (including all root categories). Bound to the
    /// "Свернуть всё" button in the status bar.
    /// </summary>
    [RelayCommand]
    private void CollapseAllTree()
    {
        CollapseAll(TreeNodes);
    }

    /// <summary>
    /// Toggles the subtree rooted at <paramref name="category"/>:
    /// expands everything if any descendant is collapsed, otherwise collapses
    /// the whole subtree. Bound to the hover-reveal button in each category
    /// header.
    /// </summary>
    [RelayCommand]
    private void ToggleSubtree(CategoryNodeViewModel? category)
    {
        if (category is null)
            return;

        if (IsAnyDescendantCollapsed(category))
            ExpandSubtree(category);
        else
            CollapseSubtree(category);
    }

    /// <summary>
    /// Принудительно сворачивает все категории в дереве, затем разворачивает только
    /// те, чьи ID присутствуют в <paramref name="savedCategoryIds"/>. Используется
    /// при очистке строки поиска, чтобы WPF TreeView визуально отразил
    /// «свёрнутое» состояние категорий, которые ранее были развёрнуты через
    /// <c>expandAll=true</c>.
    /// Без явного <see cref="CollapseAll(IEnumerable{CatalogTreeNodeViewModel})"/>
    /// новые VM-объекты приходят с <c>IsExpanded=false</c>, но ранее отрисованные
    /// TreeViewItem'ы с TwoWay-биндингом не сбрасывают визуальное состояние.
    /// Каждая категория из saved раскрывается точечно (без каскадного разворачивания
    /// потомков), чтобы не раскрывать свёрнутые братья/сёстры, которых нет в saved.
    /// </summary>
    internal static void RestoreExpandedState(IEnumerable<CatalogTreeNodeViewModel> roots, HashSet<string> savedCategoryIds)
    {
        if (savedCategoryIds is null || savedCategoryIds.Count == 0)
        {
            CollapseAll(roots);
            return;
        }

        CollapseAll(roots);

        foreach (var root in roots)
        {
            RestoreExpandedTargeted(root, savedCategoryIds);
        }
    }

    private static void RestoreExpandedTargeted(CatalogTreeNodeViewModel node, HashSet<string> savedCategoryIds)
    {
        if (node is CategoryNodeViewModel cat)
        {
            if (savedCategoryIds.Contains(cat.CategoryId))
            {
                cat.IsExpanded = true;
            }
        }

        foreach (var child in node.Children)
        {
            RestoreExpandedTargeted(child, savedCategoryIds);
        }
    }

    /// <summary>
    /// Восстанавливает развёрнутость <see cref="FamilyLeafNodeViewModel"/> по сохранённым
    /// ID. Используется в паре с <see cref="RestoreExpandedState"/> при возврате из поиска:
    /// после CollapseAll семейства нужно снова развернуть, иначе пользователь потеряет
    /// раскрытые им узлы с типами. Поиск по дереву рекурсивный, IDs сравниваются через
    /// <see cref="HashSet{T}.Contains"/>.
    /// </summary>
    internal static void RestoreExpandedFamilies(IEnumerable<CatalogTreeNodeViewModel> roots, HashSet<string> savedFamilyIds)
    {
        if (savedFamilyIds is null || savedFamilyIds.Count == 0)
        {
            return;
        }

        foreach (var root in roots)
        {
            RestoreExpandedFamiliesRecursive(root, savedFamilyIds);
        }
    }

    private static void RestoreExpandedFamiliesRecursive(CatalogTreeNodeViewModel node, HashSet<string> savedFamilyIds)
    {
        if (node is FamilyLeafNodeViewModel leaf && savedFamilyIds.Contains(leaf.CatalogItemId))
        {
            leaf.IsExpanded = true;
        }

        foreach (var child in node.Children)
        {
            RestoreExpandedFamiliesRecursive(child, savedFamilyIds);
        }
    }
}