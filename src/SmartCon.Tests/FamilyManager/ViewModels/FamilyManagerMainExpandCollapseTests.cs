using System.Collections.ObjectModel;
using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class FamilyManagerMainExpandCollapseTests
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

    private static CategoryNodeViewModel BuildThreeLevelTree()
    {
        var root = MakeCat("root", "Root");
        var mid = MakeCat("mid", "Mid");
        var leaf1 = MakeCat("leaf1", "Leaf1");
        var leaf2 = MakeCat("leaf2", "Leaf2");
        var familyA = MakeLeaf("fa", "Family A");
        var familyB = MakeLeaf("fb", "Family B");
        var familyC = MakeLeaf("fc", "Family C");

        leaf1.Children.Add(familyA);
        leaf2.Children.Add(familyB);
        mid.Children.Add(leaf1);
        mid.Children.Add(leaf2);
        mid.Children.Add(familyC);
        root.Children.Add(mid);

        return root;
    }

    [Fact]
    public void ExpandSubtree_ThreeLevels_AllExpanded_LeavesUntouched()
    {
        var root = BuildThreeLevelTree();
        root.AttachCollapseTracking();
        var leafFamily = root.Children.OfType<CategoryNodeViewModel>().First().Children.OfType<FamilyLeafNodeViewModel>().First();
        var leafFamilyIsExpandedBefore = leafFamily.IsExpanded;

        FamilyManagerMainViewModel.ExpandSubtree(root);

        Assert.True(root.IsExpanded);
        Assert.True(root.Children.OfType<CategoryNodeViewModel>().Single().IsExpanded);
        var mid = root.Children.OfType<CategoryNodeViewModel>().Single();
        Assert.All(mid.Children.OfType<CategoryNodeViewModel>(), c => Assert.True(c.IsExpanded));
        Assert.Equal(leafFamilyIsExpandedBefore, leafFamily.IsExpanded);
    }

    [Fact]
    public void CollapseSubtree_ThreeLevels_AllCollapsedIncludingRoot()
    {
        var root = BuildThreeLevelTree();
        root.AttachCollapseTracking();
        root.IsExpanded = true;
        foreach (var c in root.Children.OfType<CategoryNodeViewModel>())
        {
            c.IsExpanded = true;
            foreach (var cc in c.Children.OfType<CategoryNodeViewModel>())
                cc.IsExpanded = true;
        }

        FamilyManagerMainViewModel.CollapseSubtree(root);

        Assert.False(root.IsExpanded);
        var mid = root.Children.OfType<CategoryNodeViewModel>().Single();
        Assert.False(mid.IsExpanded);
        Assert.All(mid.Children.OfType<CategoryNodeViewModel>(), c => Assert.False(c.IsExpanded));
    }

    [Fact]
    public void ExpandAll_MultipleRoots_EveryCategoryExpanded()
    {
        var root1 = MakeCat("1", "R1");
        var child1 = MakeCat("1.1", "R1.1");
        root1.Children.Add(child1);
        root1.AttachCollapseTracking();
        var root2 = MakeCat("2", "R2");
        var child2 = MakeCat("2.1", "R2.1");
        root2.Children.Add(child2);
        root2.AttachCollapseTracking();
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { root1, root2 };

        FamilyManagerMainViewModel.ExpandAll(roots);

        Assert.True(root1.IsExpanded);
        Assert.True(child1.IsExpanded);
        Assert.True(root2.IsExpanded);
        Assert.True(child2.IsExpanded);
    }

    [Fact]
    public void CollapseAll_MultipleRoots_EveryCategoryCollapsed()
    {
        var root1 = MakeCat("1", "R1");
        var child1 = MakeCat("1.1", "R1.1");
        root1.IsExpanded = true;
        child1.IsExpanded = true;
        root1.Children.Add(child1);
        root1.AttachCollapseTracking();
        var root2 = MakeCat("2", "R2");
        var child2 = MakeCat("2.1", "R2.1");
        root2.IsExpanded = true;
        child2.IsExpanded = true;
        root2.Children.Add(child2);
        root2.AttachCollapseTracking();
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { root1, root2 };

        FamilyManagerMainViewModel.CollapseAll(roots);

        Assert.False(root1.IsExpanded);
        Assert.False(child1.IsExpanded);
        Assert.False(root2.IsExpanded);
        Assert.False(child2.IsExpanded);
    }

    [Fact]
    public void ExpandSubtree_EmptyCategory_JustSetsIsExpanded()
    {
        var cat = MakeCat("empty", "Empty");

        FamilyManagerMainViewModel.ExpandSubtree(cat);

        Assert.True(cat.IsExpanded);
    }

    [Fact]
    public void CollapseSubtree_LeafAsInput_NoCategories_NoChange()
    {
        var leaf = MakeLeaf("leaf", "Leaf");

        FamilyManagerMainViewModel.CollapseSubtree(leaf);

        Assert.False(leaf.IsExpanded);
    }

    [Fact]
    public void IsAnyDescendantCollapsed_RootCollapsed_ReturnsTrue()
    {
        var root = BuildThreeLevelTree();
        root.AttachCollapseTracking();
        root.IsExpanded = false;

        Assert.True(root.IsAnyDescendantCollapsed);
    }

    [Fact]
    public void IsAnyDescendantCollapsed_DeeplyCollapsedChild_ReturnsTrue()
    {
        var root = BuildThreeLevelTree();
        root.AttachCollapseTracking();
        root.IsExpanded = true;
        var mid = root.Children.OfType<CategoryNodeViewModel>().Single();
        mid.IsExpanded = true;
        var leaf1 = mid.Children.OfType<CategoryNodeViewModel>().First();
        leaf1.IsExpanded = false;

        Assert.True(root.IsAnyDescendantCollapsed);
    }

    [Fact]
    public void IsAnyDescendantCollapsed_AllExpanded_ReturnsFalse()
    {
        var root = BuildThreeLevelTree();
        root.AttachCollapseTracking();
        root.IsExpanded = true;
        var mid = root.Children.OfType<CategoryNodeViewModel>().Single();
        mid.IsExpanded = true;
        foreach (var c in mid.Children.OfType<CategoryNodeViewModel>())
            c.IsExpanded = true;

        Assert.False(root.IsAnyDescendantCollapsed);
    }

    [Fact]
    public void IsAnyDescendantCollapsed_NoChildren_JustRootExpanded_ReturnsFalse()
    {
        var cat = MakeCat("solo", "Solo");
        cat.AttachCollapseTracking();
        cat.IsExpanded = true;

        Assert.False(cat.IsAnyDescendantCollapsed);
    }

    [Fact]
    public void IsAnyDescendantCollapsed_NoChildren_RootCollapsed_ReturnsTrue()
    {
        var cat = MakeCat("solo", "Solo");
        cat.AttachCollapseTracking();
        cat.IsExpanded = false;

        Assert.True(cat.IsAnyDescendantCollapsed);
    }

    [Fact]
    public void IsAnyDescendantCollapsed_ReactsToDeepDescendantChange()
    {
        var root = BuildThreeLevelTree();
        root.AttachCollapseTracking();
        root.IsExpanded = true;
        var mid = root.Children.OfType<CategoryNodeViewModel>().Single();
        mid.IsExpanded = true;
        foreach (var c in mid.Children.OfType<CategoryNodeViewModel>())
            c.IsExpanded = true;

        Assert.False(root.IsAnyDescendantCollapsed);

        var deepLeaf = mid.Children.OfType<CategoryNodeViewModel>().First();
        deepLeaf.IsExpanded = false;

        Assert.True(root.IsAnyDescendantCollapsed);
    }

    [Fact]
    public void CollectExpandedIds_DeepTree_CollectsOnlyExpandedNodes()
    {
        var root = MakeCat("root", "Root");
        var mid = MakeCat("mid", "Mid");
        var leaf1 = MakeCat("leaf1", "Leaf1");
        var leaf2 = MakeCat("leaf2", "Leaf2");
        var familyA = MakeLeaf("fa", "Family A");
        var familyB = MakeLeaf("fb", "Family B");

        leaf1.Children.Add(familyA);
        leaf2.Children.Add(familyB);
        mid.Children.Add(leaf1);
        mid.Children.Add(leaf2);
        root.Children.Add(mid);

        root.IsExpanded = true;
        mid.IsExpanded = true;
        leaf1.IsExpanded = true;
        familyA.IsExpanded = true;

        var catIds = new HashSet<string>();
        var famIds = new HashSet<string>();
        FamilyManagerMainViewModel.CollectExpandedIds(
            new ObservableCollection<CatalogTreeNodeViewModel> { root }, catIds, famIds);

        Assert.Contains("root", catIds);
        Assert.Contains("mid", catIds);
        Assert.Contains("leaf1", catIds);
        Assert.DoesNotContain("leaf2", catIds);
        Assert.Contains("fa", famIds);
        Assert.DoesNotContain("fb", famIds);
    }
}
