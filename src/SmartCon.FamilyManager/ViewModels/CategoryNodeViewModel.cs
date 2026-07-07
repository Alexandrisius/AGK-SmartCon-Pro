using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => true;

    public string CategoryId { get; set; }
    public string? ParentId { get; set; }
    public string FullPath { get; set; }
    public int SortOrder { get; set; }

    public bool IsNew { get; set; }
    public bool IsDirty { get; set; }
    public bool IsDeleted { get; set; }
    public string OriginalName { get; set; } = string.Empty;
    public string? OriginalParentId { get; set; }
    public int OriginalSortOrder { get; set; }

    [ObservableProperty] private int _familyCount;

    /// <summary>Roll-up: true if any leaf under this category is stale (recursive).</summary>
    [ObservableProperty] private bool _hasStale;

    /// <summary>Roll-up: number of stale leaves under this category (recursive).</summary>
    [ObservableProperty] private int _staleCount;

    /// <summary>
    /// True if this category or any descendant category is collapsed.
    /// Used by the hover-reveal toggle button in the category header to switch
    /// between "expand" and "collapse" icons. Computed reactively: subscribes
    /// to PropertyChanged of this node and all descendant CategoryNodeViewModels,
    /// recalculates on IsExpanded changes anywhere in the subtree.
    /// </summary>
    [ObservableProperty] private bool _isAnyDescendantCollapsed = true;

    public CategoryNodeViewModel(CategoryNode node)
    {
        CategoryId = node.Id;
        ParentId = node.ParentId;
        DisplayName = node.Name;
        OriginalName = node.Name;
        FullPath = node.FullPath;
        SortOrder = node.SortOrder;
        OriginalSortOrder = node.SortOrder;
        OriginalParentId = node.ParentId;
        PropertyChanged += OnSelfPropertyChanged;
    }

    public CategoryNodeViewModel(string categoryId, string name, string? parentId, string fullPath)
    {
        CategoryId = categoryId;
        ParentId = parentId;
        DisplayName = name;
        OriginalName = name;
        FullPath = fullPath;
        IsNew = true;
        IsDirty = true;
        PropertyChanged += OnSelfPropertyChanged;
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsExpanded))
            RecomputeIsAnyDescendantCollapsed();
    }

    private void RecomputeIsAnyDescendantCollapsed()
    {
        var current = ComputeIsAnyDescendantCollapsed();
        if (IsAnyDescendantCollapsed != current)
            IsAnyDescendantCollapsed = current;
    }

    private bool ComputeIsAnyDescendantCollapsed()
    {
        if (!IsExpanded)
            return true;

        foreach (var child in Children)
        {
            if (child is CategoryNodeViewModel catChild && catChild.IsAnyDescendantCollapsed)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Hook called from <see cref="FamilyManagerMainViewModel.BuildCategoryNode"/> after the
    /// subtree is fully assembled. Wires up reactive collapse-state tracking across the
    /// subtree by subscribing to PropertyChanged of every descendant CategoryNodeViewModel
    /// and to Children.CollectionChanged so newly added/removed descendants update tracking.
    /// Ancestors are notified automatically because each parent subscribes to its direct
    /// children — when a deep descendant toggles IsExpanded, the change propagates up the
    /// chain one hop at a time.
    /// </summary>
    internal void AttachCollapseTracking()
    {
        Children.CollectionChanged += OnChildrenCollectionChanged;
        foreach (var child in Children)
        {
            if (child is CategoryNodeViewModel catChild)
                AttachToDescendant(catChild);
        }
        RecomputeIsAnyDescendantCollapsed();
    }

    internal void DetachCollapseTracking()
    {
        Children.CollectionChanged -= OnChildrenCollectionChanged;
        foreach (var child in Children)
        {
            if (child is CategoryNodeViewModel catChild)
                DetachFromDescendant(catChild);
        }
    }

    private void OnChildrenCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is CategoryNodeViewModel cat)
                    AttachToDescendant(cat);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is CategoryNodeViewModel cat)
                    DetachFromDescendant(cat);
            }
        }

        RecomputeIsAnyDescendantCollapsed();
    }

    private void AttachToDescendant(CategoryNodeViewModel descendant)
    {
        descendant.PropertyChanged += OnDescendantPropertyChanged;
        descendant.AttachCollapseTracking();
    }

    private void DetachFromDescendant(CategoryNodeViewModel descendant)
    {
        descendant.PropertyChanged -= OnDescendantPropertyChanged;
        descendant.DetachCollapseTracking();
    }

    private void OnDescendantPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsExpanded) || e.PropertyName == nameof(IsAnyDescendantCollapsed))
            RecomputeIsAnyDescendantCollapsed();
    }
}
