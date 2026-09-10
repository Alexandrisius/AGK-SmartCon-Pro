using System.Collections.ObjectModel;
using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
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
        return new FamilyLeafNodeViewModel(row, new Mock<IFamilyAssetService>().Object);
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

    [Fact]
    public void AttachTypesToNodes_DefaultMarker_CreatesVirtualNodeWithFamilyName()
    {
        // #172: the synthetic <default> row (hash stability, ADR-049) must surface
        // in the tree as the v2.0.0 virtual node — family name + IsVirtual — so
        // Place/DnD take LoadFamilyAsync instead of LoadFamilySymbol("<default>").
        var leaf = MakeLeaf("a", "Опора корпусная приварная КП");
        var batch = new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>
        {
            ["a"] = [new FamilyTypeDescriptor("t1", "a", FamilyTypeSnapshot.DefaultTypeName, 0)],
        };

        FamilyManagerMainViewModel.AttachTypesToNodes(
            new ObservableCollection<CatalogTreeNodeViewModel> { leaf },
            batch,
            new HashSet<string>());

        var typeNode = Assert.IsType<FamilyTypeNodeViewModel>(Assert.Single(leaf.Children));
        Assert.True(typeNode.IsVirtual);
        Assert.Equal(leaf.DisplayName, typeNode.TypeName);
        Assert.Equal(leaf.DisplayName, typeNode.DisplayName);
        Assert.Null(typeNode.UniqueId);
    }

    [Fact]
    public void AttachTypesToNodes_NamedType_CreatesNonVirtualNode()
    {
        var leaf = MakeLeaf("a", "Fam A");
        var batch = new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>
        {
            ["a"] = [new FamilyTypeDescriptor("t1", "a", "Ду 100", 0, UniqueId: "uid-1")],
        };

        FamilyManagerMainViewModel.AttachTypesToNodes(
            new ObservableCollection<CatalogTreeNodeViewModel> { leaf },
            batch,
            new HashSet<string>());

        var typeNode = Assert.IsType<FamilyTypeNodeViewModel>(Assert.Single(leaf.Children));
        Assert.False(typeNode.IsVirtual);
        Assert.Equal("Ду 100", typeNode.TypeName);
        Assert.Equal("Ду 100", typeNode.DisplayName);
        Assert.Equal("uid-1", typeNode.UniqueId);
    }

    [Fact]
    public void AttachTypesToNodes_NoTypes_CreatesVirtualFallback()
    {
        var leaf = MakeLeaf("a", "Fam A");
        var batch = new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>();

        FamilyManagerMainViewModel.AttachTypesToNodes(
            new ObservableCollection<CatalogTreeNodeViewModel> { leaf },
            batch,
            new HashSet<string>());

        var typeNode = Assert.IsType<FamilyTypeNodeViewModel>(Assert.Single(leaf.Children));
        Assert.True(typeNode.IsVirtual);
        Assert.Equal(leaf.DisplayName, typeNode.TypeName);
    }
}
