using System.Collections.Generic;
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
        var result = agg.AggregateByCategory([], new Dictionary<string, IReadOnlyList<string>>());
        Assert.Empty(result);
    }

    [Fact]
    public void AggregateByCategory_AllFresh_NoCategoryMarked()
    {
        var agg = new StaleCategoryAggregator();
        var results = new List<StaleCheckResult> { Fresh("a"), Fresh("b") };
        var idx = new Dictionary<string, IReadOnlyList<string>>
        {
            ["a"] = new List<string> { "cat1" },
            ["b"] = new List<string> { "cat2" },
        };
        var result = agg.AggregateByCategory(results, idx);
        Assert.Empty(result);
    }

    [Fact]
    public void AggregateByCategory_OneStaleInCategory_CategoryMarked()
    {
        var agg = new StaleCategoryAggregator();
        var results = new List<StaleCheckResult> { Stale("a", StaleReason.NoEntityStorage) };
        var idx = new Dictionary<string, IReadOnlyList<string>>
        {
            ["a"] = new List<string> { "cat1" },
        };
        var result = agg.AggregateByCategory(results, idx);
        Assert.Single(result);
        Assert.True(result["cat1"]);
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
        var leaf = new FamilyLeafNodeViewModel(leafRow);
        sub.Children.Add(leaf);
        root.Children.Add(sub);

        var adapterRoots = CategoryTreeAdapter.AdaptRoots(new[] { root });
        var agg = new StaleCategoryAggregator();
        var map = agg.BuildCatalogToCategoryMap(new[] { "c1" }, adapterRoots);

        Assert.True(map.ContainsKey("c1"));
        Assert.Contains("root", map["c1"]);
        Assert.Contains("sub", map["c1"]);
    }
}
