using System.Collections.Generic;
using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Stale;

public class StaleCategoryAggregatorTests
{
    private static StaleCheckResult Stale(string id, StaleReason r) =>
        new(id, $"Family-{id}", null, null, true, r);

    private static StaleCheckResult Fresh(string id) =>
        new(id, $"Family-{id}", null, null, false, StaleReason.None);

    [Fact]
    public void AggregateByCategory_EmptyResults_EmptyDict()
    {
        var agg = new StaleCategoryAggregator();
        var result = agg.AggregateByCategory(
            [],
            new Dictionary<string, IReadOnlyCollection<string>>(),
            new HashSet<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void AggregateByCategory_AllFresh_NoCategoryMarked()
    {
        var agg = new StaleCategoryAggregator();
        var results = new List<StaleCheckResult> { Fresh("a"), Fresh("b") };
        var map = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["a"] = new HashSet<string> { "cat1" },
            ["b"] = new HashSet<string> { "cat2" },
        };
        var staleIds = new HashSet<string>(); // empty
        var result = agg.AggregateByCategory(results, map, staleIds);
        Assert.Empty(result);
    }

    [Fact]
    public void AggregateByCategory_OneStaleInCategory_CategoryMarked()
    {
        var agg = new StaleCategoryAggregator();
        var results = new List<StaleCheckResult> { Stale("a", StaleReason.NoEntityStorage) };
        var map = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["a"] = new HashSet<string> { "cat1" },
        };
        var staleIds = new HashSet<string> { "a" };
        var result = agg.AggregateByCategory(results, map, staleIds);
        Assert.Single(result);
        Assert.True(result["cat1"].HasStale);
        Assert.Equal(1, result["cat1"].StaleCount);
    }

    [Fact]
    public void AggregateByCategory_MultipleStaleInCategory_CountIsSum()
    {
        var agg = new StaleCategoryAggregator();
        var results = new List<StaleCheckResult>
        {
            Stale("a", StaleReason.NoEntityStorage),
            Stale("b", StaleReason.VersionMismatch),
            Stale("c", StaleReason.NoEntityStorage),
        };
        var map = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["a"] = new HashSet<string> { "cat1" },
            ["b"] = new HashSet<string> { "cat1" },
            ["c"] = new HashSet<string> { "cat1" },
        };
        var staleIds = new HashSet<string> { "a", "b", "c" };
        var result = agg.AggregateByCategory(results, map, staleIds);
        Assert.Single(result);
        Assert.True(result["cat1"].HasStale);
        Assert.Equal(3, result["cat1"].StaleCount);
    }

    [Fact]
    public void AggregateByCategory_StaleInUncategorized_MarksSyntheticId()
    {
        // "Без категории" surfaces in the tree as a synthetic category with id "__no_category__".
        // The aggregator must roll up the same way as for any other category.
        var agg = new StaleCategoryAggregator();
        var results = new List<StaleCheckResult>
        {
            Stale("u1", StaleReason.NoEntityStorage),
        };
        var map = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["u1"] = new HashSet<string> { "__no_category__" },
        };
        var staleIds = new HashSet<string> { "u1" };
        var result = agg.AggregateByCategory(results, map, staleIds);
        Assert.True(result["__no_category__"].HasStale);
        Assert.Equal(1, result["__no_category__"].StaleCount);
    }

    [Fact]
    public void AggregateByCategory_StaleInParentAndChild_BothMarked()
    {
        // Stale family under a child category must also mark the parent.
        var agg = new StaleCategoryAggregator();
        var results = new List<StaleCheckResult>
        {
            Stale("a", StaleReason.VersionMismatch),
        };
        var map = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["a"] = new HashSet<string> { "root", "sub" },
        };
        var staleIds = new HashSet<string> { "a" };
        var result = agg.AggregateByCategory(results, map, staleIds);
        Assert.True(result["root"].HasStale);
        Assert.True(result["sub"].HasStale);
        // The leaf is counted in the closest category, not in the parent.
        // (This is by design: StaleCount tracks items directly assigned.)
        Assert.Equal(1, result["sub"].StaleCount);
        Assert.Equal(1, result["root"].StaleCount);
    }

    [Fact]
    public void BuildCatalogToCategoryMap_RecursiveParents_AllListed()
    {
        // tree:
        // root
        //  └── sub
        //       └── leaf(family=c1)
        var now = DateTimeOffset.UtcNow;
        var rootNode = new CategoryNode("root", "Root", null, 0, "Root", now);
        var subNode = new CategoryNode("sub", "Sub", "root", 0, "Root/Sub", now);
        var root = new CategoryNodeViewModel(rootNode);
        var sub = new CategoryNodeViewModel(subNode);
        var leafRow = new FamilyCatalogItemRow { Id = "c1", Name = "Family-c1" };
        var leaf = new FamilyLeafNodeViewModel(leafRow, new Mock<IFamilyAssetService>().Object);
        sub.Children.Add(leaf);
        root.Children.Add(sub);

        var adapterRoots = CategoryTreeAdapter.AdaptRoots(new[] { root });
        var agg = new StaleCategoryAggregator();
        var map = agg.BuildCatalogToCategoryMap(new[] { "c1" }, adapterRoots);

        Assert.True(map.ContainsKey("c1"));
        Assert.Contains("root", map["c1"]);
        Assert.Contains("sub", map["c1"]);
    }

    [Fact]
    public void BuildCatalogToCategoryMap_DeepNesting_AllAncestorsListed()
    {
        // root
        //  └── a
        //       └── b
        //            └── c
        //                 └── leaf(family=deep1)
        var now = DateTimeOffset.UtcNow;
        var root = new CategoryNodeViewModel(new CategoryNode("root", "Root", null, 0, "Root", now));
        var a = new CategoryNodeViewModel(new CategoryNode("a", "A", "root", 0, "Root/A", now));
        var b = new CategoryNodeViewModel(new CategoryNode("b", "B", "a", 0, "Root/A/B", now));
        var c = new CategoryNodeViewModel(new CategoryNode("c", "C", "b", 0, "Root/A/B/C", now));
        var leaf = new FamilyLeafNodeViewModel(new FamilyCatalogItemRow { Id = "deep1", Name = "Family-deep1" }, new Mock<IFamilyAssetService>().Object);
        c.Children.Add(leaf);
        b.Children.Add(c);
        a.Children.Add(b);
        root.Children.Add(a);

        var adapterRoots = CategoryTreeAdapter.AdaptRoots(new[] { root });
        var agg = new StaleCategoryAggregator();
        var map = agg.BuildCatalogToCategoryMap(new[] { "deep1" }, adapterRoots);

        Assert.Contains("root", map["deep1"]);
        Assert.Contains("a", map["deep1"]);
        Assert.Contains("b", map["deep1"]);
        Assert.Contains("c", map["deep1"]);
    }

    [Fact]
    public void BuildCatalogToCategoryMap_UncategorizedAsCategory()
    {
        // "Без категории" leaf attaches to a synthetic "__no_category__" parent.
        var now = DateTimeOffset.UtcNow;
        var noCat = new CategoryNodeViewModel(new CategoryNode("__no_category__", "Без категории", null, 0, "Без категории", now));
        var leaf = new FamilyLeafNodeViewModel(new FamilyCatalogItemRow { Id = "u1", Name = "Family-u1" }, new Mock<IFamilyAssetService>().Object);
        noCat.Children.Add(leaf);

        var adapterRoots = CategoryTreeAdapter.AdaptRoots(new[] { noCat });
        var agg = new StaleCategoryAggregator();
        var map = agg.BuildCatalogToCategoryMap(new[] { "u1" }, adapterRoots);

        Assert.Contains("__no_category__", map["u1"]);
    }

    [Fact]
    public void BuildCatalogToCategoryMap_EmptyCatalogItemIds_EmptyMap()
    {
        var agg = new StaleCategoryAggregator();
        var map = agg.BuildCatalogToCategoryMap([], []);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildCatalogToCategoryMap_CatalogItemNotInTree_EmptyResult()
    {
        var now = DateTimeOffset.UtcNow;
        var root = new CategoryNodeViewModel(new CategoryNode("root", "Root", null, 0, "Root", now));
        var adapterRoots = CategoryTreeAdapter.AdaptRoots(new[] { root });
        var agg = new StaleCategoryAggregator();
        var map = agg.BuildCatalogToCategoryMap(new[] { "unknown-id" }, adapterRoots);

        Assert.True(map.ContainsKey("unknown-id"));
        Assert.Empty(map["unknown-id"]);
    }
}
