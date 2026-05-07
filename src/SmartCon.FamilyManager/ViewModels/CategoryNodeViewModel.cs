using System.Collections.ObjectModel;
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
    }
}
