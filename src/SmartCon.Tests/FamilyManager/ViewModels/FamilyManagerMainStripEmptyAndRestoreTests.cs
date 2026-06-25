using System.Collections.ObjectModel;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class FamilyManagerMainStripEmptyCategoriesTests
{
    private static FamilyLeafNodeViewModel MakeLeaf(string id, string name)
    {
        var row = new FamilyCatalogItemRow { Id = id, Name = name };
        return new FamilyLeafNodeViewModel(row);
    }

    private static CategoryNodeViewModel MakeCat(string id, string name)
    {
        return new CategoryNodeViewModel(new CategoryNode(id, name, null, 0, name, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void StripEmptyCategories_NullRoots_DoesNotThrow()
    {
        var ex = Record.Exception(() => FamilyManagerMainViewModel.StripEmptyCategories(null!, expandAll: true));
        Assert.Null(ex);
    }

    [Fact]
    public void StripEmptyCategories_SearchInactive_DoesNotRemoveAnything()
    {
        var catA = MakeCat("A", "A");
        catA.FamilyCount = 0;
        var catB = MakeCat("B", "B");
        catB.FamilyCount = 5;
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { catA, catB };

        FamilyManagerMainViewModel.StripEmptyCategories(roots, expandAll: false);

        Assert.Equal(2, roots.Count);
    }

    [Fact]
    public void StripEmptyCategories_EmptyCategoryWithNoFamilies_IsRemoved()
    {
        var emptyCat = MakeCat("empty", "Empty");
        emptyCat.FamilyCount = 0;
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { emptyCat };

        FamilyManagerMainViewModel.StripEmptyCategories(roots, expandAll: true);

        Assert.Empty(roots);
    }

    [Fact]
    public void StripEmptyCategories_CategoryWithFamilies_IsKept()
    {
        var cat = MakeCat("cat", "Cat");
        cat.Children.Add(MakeLeaf("a", "Filter"));
        cat.FamilyCount = 1;
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { cat };

        FamilyManagerMainViewModel.StripEmptyCategories(roots, expandAll: true);

        Assert.Single(roots);
        Assert.Same(cat, roots[0]);
    }

    [Fact]
    public void StripEmptyCategories_NestedEmptyChildren_AreRemovedAndParentIsRemoved()
    {
        var root = MakeCat("root", "Root");
        var emptyMid = MakeCat("mid", "Mid");
        emptyMid.FamilyCount = 0;
        var emptyLeaf = MakeCat("leaf", "Leaf");
        emptyLeaf.FamilyCount = 0;
        emptyMid.Children.Add(emptyLeaf);
        root.Children.Add(emptyMid);
        root.FamilyCount = 0;

        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { root };

        FamilyManagerMainViewModel.StripEmptyCategories(roots, expandAll: true);

        Assert.Empty(roots);
    }

    [Fact]
    public void StripEmptyCategories_ParentEmptyButChildHasFamilies_IsKept()
    {
        var root = MakeCat("root", "Root");
        root.FamilyCount = 0;
        var mid = MakeCat("mid", "Mid");
        mid.FamilyCount = 2;
        mid.Children.Add(MakeLeaf("a", "Filter A"));
        mid.Children.Add(MakeLeaf("b", "Filter B"));
        root.Children.Add(mid);

        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { root };

        FamilyManagerMainViewModel.StripEmptyCategories(roots, expandAll: true);

        Assert.Single(roots);
        Assert.Same(root, roots[0]);
        Assert.Single(root.Children);
        Assert.Same(mid, root.Children[0]);
    }

    [Fact]
    public void StripEmptyCategories_MixedEmptyAndNonEmpty_OnlyEmptyRemoved()
    {
        var keep = MakeCat("keep", "Keep");
        keep.Children.Add(MakeLeaf("a", "Filter"));
        keep.FamilyCount = 1;
        var drop = MakeCat("drop", "Drop");
        drop.FamilyCount = 0;
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { keep, drop };

        FamilyManagerMainViewModel.StripEmptyCategories(roots, expandAll: true);

        Assert.Single(roots);
        Assert.Same(keep, roots[0]);
    }

    [Fact]
    public void StripEmptyCategories_LeavesAreNotRemoved()
    {
        var leaf = MakeLeaf("a", "Filter");
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { leaf };

        FamilyManagerMainViewModel.StripEmptyCategories(roots, expandAll: true);

        Assert.Single(roots);
        Assert.Same(leaf, roots[0]);
    }

    [Fact]
    public void StripEmptyCategories_DeeplyNestedParentKeepsNonEmptyBranch()
    {
        var root = MakeCat("root", "Root");
        var branch1 = MakeCat("b1", "Branch1");
        var branch2 = MakeCat("b2", "Branch2");
        branch2.Children.Add(MakeLeaf("a", "Filter"));
        branch2.FamilyCount = 1;
        root.Children.Add(branch1);
        root.Children.Add(branch2);
        root.FamilyCount = 0;

        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { root };

        FamilyManagerMainViewModel.StripEmptyCategories(roots, expandAll: true);

        Assert.Single(roots);
        Assert.Same(root, roots[0]);
        Assert.Single(root.Children);
        Assert.Same(branch2, root.Children[0]);
    }

    [Fact]
    public void StripEmptyCategories_AllEmpty_AllRemoved()
    {
        var c1 = MakeCat("c1", "C1");
        c1.FamilyCount = 0;
        var c2 = MakeCat("c2", "C2");
        c2.FamilyCount = 0;
        var c3 = MakeCat("c3", "C3");
        c3.FamilyCount = 0;
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { c1, c2, c3 };

        FamilyManagerMainViewModel.StripEmptyCategories(roots, expandAll: true);

        Assert.Empty(roots);
    }
}

public sealed class FamilyManagerMainRestoreExpandedStateTests
{
    private static FamilyLeafNodeViewModel MakeLeaf(string id, string name)
    {
        var row = new FamilyCatalogItemRow { Id = id, Name = name };
        return new FamilyLeafNodeViewModel(row);
    }

    private static CategoryNodeViewModel MakeCat(string id, string name)
    {
        return new CategoryNodeViewModel(new CategoryNode(id, name, null, 0, name, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RestoreExpandedState_EmptySavedIds_CollapsesEverything()
    {
        var cat = MakeCat("cat", "Cat");
        cat.IsExpanded = true;
        cat.Children.Add(MakeLeaf("a", "Filter"));
        cat.AttachCollapseTracking();
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { cat };

        FamilyManagerMainViewModel.RestoreExpandedState(roots, new HashSet<string>());

        Assert.False(cat.IsExpanded);
    }

    [Fact]
    public void RestoreExpandedState_SavedIdMatches_ExpandsThatCategory()
    {
        var cat = MakeCat("cat", "Cat");
        cat.IsExpanded = true;
        cat.Children.Add(MakeLeaf("a", "Filter"));
        cat.AttachCollapseTracking();
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { cat };

        FamilyManagerMainViewModel.RestoreExpandedState(roots, new HashSet<string> { "cat" });

        Assert.True(cat.IsExpanded);
    }

    [Fact]
    public void RestoreExpandedState_SavedIdMissing_CollapsesCategory()
    {
        var cat = MakeCat("cat", "Cat");
        cat.IsExpanded = true;
        cat.Children.Add(MakeLeaf("a", "Filter"));
        cat.AttachCollapseTracking();
        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { cat };

        FamilyManagerMainViewModel.RestoreExpandedState(roots, new HashSet<string> { "other" });

        Assert.False(cat.IsExpanded);
    }

    [Fact]
    public void RestoreExpandedState_NestedSavedIds_ExpandAllAncestorsAndMatchingLeaf()
    {
        var root = MakeCat("root", "Root");
        var mid = MakeCat("mid", "Mid");
        var leaf1 = MakeCat("leaf1", "Leaf1");
        var leaf2 = MakeCat("leaf2", "Leaf2");
        leaf1.Children.Add(MakeLeaf("a", "Filter A"));
        leaf1.FamilyCount = 1;
        leaf2.FamilyCount = 0;
        mid.Children.Add(leaf1);
        mid.Children.Add(leaf2);
        root.Children.Add(mid);
        root.AttachCollapseTracking();
        root.IsExpanded = true;
        mid.IsExpanded = true;
        leaf1.IsExpanded = true;

        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { root };

        FamilyManagerMainViewModel.RestoreExpandedState(
            roots,
            new HashSet<string> { "root", "mid", "leaf1" });

        Assert.True(root.IsExpanded);
        Assert.True(mid.IsExpanded);
        Assert.True(leaf1.IsExpanded);
        Assert.False(leaf2.IsExpanded);
    }

    [Fact]
    public void RestoreExpandedState_OnlyMatchingLeafInSaved_OnlyLeafIsExpanded()
    {
        var root = MakeCat("root", "Root");
        var mid = MakeCat("mid", "Mid");
        var leaf1 = MakeCat("leaf1", "Leaf1");
        var leaf2 = MakeCat("leaf2", "Leaf2");
        leaf1.Children.Add(MakeLeaf("a", "Filter A"));
        leaf1.FamilyCount = 1;
        leaf2.FamilyCount = 0;
        mid.Children.Add(leaf1);
        mid.Children.Add(leaf2);
        root.Children.Add(mid);
        root.AttachCollapseTracking();

        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { root };

        FamilyManagerMainViewModel.RestoreExpandedState(roots, new HashSet<string> { "leaf1" });

        Assert.False(root.IsExpanded);
        Assert.False(mid.IsExpanded);
        Assert.True(leaf1.IsExpanded);
        Assert.False(leaf2.IsExpanded);
    }

    [Fact]
    public void RestoreExpandedState_MultipleRootsWithMixedSavedIds_OnlyMatchingExpanded()
    {
        var root1 = MakeCat("r1", "R1");
        root1.Children.Add(MakeLeaf("a", "Filter A"));
        root1.FamilyCount = 1;
        root1.AttachCollapseTracking();
        root1.IsExpanded = true;

        var root2 = MakeCat("r2", "R2");
        root2.Children.Add(MakeLeaf("b", "Filter B"));
        root2.FamilyCount = 1;
        root2.AttachCollapseTracking();
        root2.IsExpanded = true;

        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { root1, root2 };

        FamilyManagerMainViewModel.RestoreExpandedState(roots, new HashSet<string> { "r1" });

        Assert.True(root1.IsExpanded);
        Assert.False(root2.IsExpanded);
    }

    [Fact]
    public void RestoreExpandedState_CollapsesBeforeExpanding_PreventsWpfVisualStaleState()
    {
        var cat = MakeCat("cat", "Cat");
        cat.Children.Add(MakeLeaf("a", "Filter"));
        cat.AttachCollapseTracking();
        cat.IsExpanded = true;

        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { cat };

        FamilyManagerMainViewModel.RestoreExpandedState(roots, new HashSet<string> { "cat" });

        Assert.True(cat.IsExpanded);
    }

    [Fact]
    public void RestoreExpandedState_NotInSaved_NorChildInSaved_CollapsesAll()
    {
        var root = MakeCat("root", "Root");
        var child = MakeCat("child", "Child");
        child.Children.Add(MakeLeaf("a", "Filter"));
        child.FamilyCount = 1;
        root.Children.Add(child);
        root.FamilyCount = 0;
        root.AttachCollapseTracking();
        root.IsExpanded = true;
        child.IsExpanded = true;

        var roots = new ObservableCollection<CatalogTreeNodeViewModel> { root };

        FamilyManagerMainViewModel.RestoreExpandedState(roots, new HashSet<string>());

        Assert.False(root.IsExpanded);
        Assert.False(child.IsExpanded);
    }
}
