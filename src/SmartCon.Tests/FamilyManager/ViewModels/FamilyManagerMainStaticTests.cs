using System.Collections.ObjectModel;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class FamilyManagerMainStaticTests
{
    private static FamilyLeafNodeViewModel MakeLeaf(string id, string name)
    {
        var row = new FamilyCatalogItemRow
        {
            Id = id,
            Name = name,
        };
        return new FamilyLeafNodeViewModel(row);
    }

    private static CategoryNodeViewModel MakeCat(string id, string name)
    {
        return new CategoryNodeViewModel(new CategoryNode(id, name, null, 0, name, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CountFamiliesRecursive_EmptyNode_ReturnsZero()
    {
        var cat = MakeCat("1", "Root");
        Assert.Equal(0, FamilyManagerMainViewModel.CountFamiliesRecursive(cat));
    }

    [Fact]
    public void CountFamiliesRecursive_WithLeaves_ReturnsCount()
    {
        var cat = MakeCat("1", "Root");
        cat.Children.Add(MakeLeaf("a", "Family A"));
        cat.Children.Add(MakeLeaf("b", "Family B"));

        Assert.Equal(2, FamilyManagerMainViewModel.CountFamiliesRecursive(cat));
    }

    [Fact]
    public void CountFamiliesRecursive_NestedCategories_CountsAll()
    {
        var root = MakeCat("1", "Root");
        var child = MakeCat("2", "Child");
        root.Children.Add(child);
        root.Children.Add(MakeLeaf("a", "Family A"));
        child.Children.Add(MakeLeaf("b", "Family B"));
        child.Children.Add(MakeLeaf("c", "Family C"));

        Assert.Equal(3, FamilyManagerMainViewModel.CountFamiliesRecursive(root));
    }

    [Fact]
    public void FindParentOf_DirectChild_ReturnsParent()
    {
        var root = MakeCat("1", "Root");
        var leaf = MakeLeaf("a", "Family A");
        root.Children.Add(leaf);

        var result = FamilyManagerMainViewModel.FindParentOf(
            new ObservableCollection<CatalogTreeNodeViewModel> { root }, leaf);

        Assert.Same(root, result);
    }

    [Fact]
    public void FindParentOf_DeepChild_ReturnsIntermediateParent()
    {
        var root = MakeCat("1", "Root");
        var child = MakeCat("2", "Child");
        var leaf = MakeLeaf("a", "Family A");
        child.Children.Add(leaf);
        root.Children.Add(child);

        var result = FamilyManagerMainViewModel.FindParentOf(
            new ObservableCollection<CatalogTreeNodeViewModel> { root }, leaf);

        Assert.Same(child, result);
    }

    [Fact]
    public void FindParentOf_NotFound_ReturnsNull()
    {
        var root = MakeCat("1", "Root");
        var orphan = MakeLeaf("x", "Orphan");

        var result = FamilyManagerMainViewModel.FindParentOf(
            new ObservableCollection<CatalogTreeNodeViewModel> { root }, orphan);

        Assert.Null(result);
    }

    [Fact]
    public void CollectFamilyIds_GathersAllIds()
    {
        var root = MakeCat("1", "Root");
        var child = MakeCat("2", "Child");
        root.Children.Add(MakeLeaf("a", "Fam A"));
        child.Children.Add(MakeLeaf("b", "Fam B"));
        root.Children.Add(child);

        var ids = new List<string>();
        FamilyManagerMainViewModel.CollectFamilyIds(
            new ObservableCollection<CatalogTreeNodeViewModel> { root }, ids);

        Assert.Equal(2, ids.Count);
        Assert.Contains("a", ids);
        Assert.Contains("b", ids);
    }

    [Fact]
    public void CollectFamilyIds_NoLeaves_ReturnsEmpty()
    {
        var root = MakeCat("1", "Root");
        var ids = new List<string>();
        FamilyManagerMainViewModel.CollectFamilyIds(
            new ObservableCollection<CatalogTreeNodeViewModel> { root }, ids);

        Assert.Empty(ids);
    }
}
