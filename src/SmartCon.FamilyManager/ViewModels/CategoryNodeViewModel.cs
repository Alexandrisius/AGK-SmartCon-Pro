using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => true;

    public string CategoryId { get; }
    public string? ParentId { get; }
    public string FullPath { get; }
    public int SortOrder { get; set; }

    [ObservableProperty] private int _familyCount;

    public CategoryNodeViewModel(CategoryNode node)
    {
        CategoryId = node.Id;
        ParentId = node.ParentId;
        DisplayName = node.Name;
        FullPath = node.FullPath;
        SortOrder = node.SortOrder;
    }

    public CategoryNodeViewModel(string categoryId, string name, string? parentId, string fullPath)
    {
        CategoryId = categoryId;
        ParentId = parentId;
        DisplayName = name;
        FullPath = fullPath;
    }
}
