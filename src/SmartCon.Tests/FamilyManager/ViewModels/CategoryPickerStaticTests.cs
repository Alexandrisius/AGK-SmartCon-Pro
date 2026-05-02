using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class CategoryPickerStaticTests
{
    private static CategoryNode MakeNode(string id, string name, string? parentId = null) =>
        new(id, name, parentId, 0, parentId is null ? name : $"{parentId}/{name}", DateTimeOffset.UtcNow);

    [Fact]
    public void FilterNodesWithAncestors_MatchingNode_IncludesAncestors()
    {
        var nodes = new List<CategoryNode>
        {
            MakeNode("1", "Pipes"),
            MakeNode("2", "Steel Pipes", "1"),
            MakeNode("3", "Copper Pipes", "1"),
        };
        var tree = new CategoryTree(nodes);

        var result = CategoryPickerViewModel.FilterNodesWithAncestors(tree, "steel");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.Id == "1");
        Assert.Contains(result, n => n.Id == "2");
    }

    [Fact]
    public void FilterNodesWithAncestors_NoMatch_ReturnsEmpty()
    {
        var nodes = new List<CategoryNode>
        {
            MakeNode("1", "Pipes"),
        };
        var tree = new CategoryTree(nodes);

        var result = CategoryPickerViewModel.FilterNodesWithAncestors(tree, "xyz");

        Assert.Empty(result);
    }

    [Fact]
    public void FilterNodesWithAncestors_RootMatch_ReturnsOnlyRoot()
    {
        var nodes = new List<CategoryNode>
        {
            MakeNode("1", "Pipes"),
            MakeNode("2", "Steel", "1"),
        };
        var tree = new CategoryTree(nodes);

        var result = CategoryPickerViewModel.FilterNodesWithAncestors(tree, "pipes");

        Assert.Single(result);
        Assert.Equal("1", result[0].Id);
    }

    [Fact]
    public void FilterNodesWithAncestors_DeepMatch_IncludesAllAncestors()
    {
        var nodes = new List<CategoryNode>
        {
            MakeNode("1", "A"),
            MakeNode("2", "B", "1"),
            MakeNode("3", "C", "2"),
        };
        var tree = new CategoryTree(nodes);

        var result = CategoryPickerViewModel.FilterNodesWithAncestors(tree, "C");

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void FilterNodesWithAncestors_CaseInsensitive()
    {
        var nodes = new List<CategoryNode>
        {
            MakeNode("1", "PIPES"),
        };
        var tree = new CategoryTree(nodes);

        var result = CategoryPickerViewModel.FilterNodesWithAncestors(tree, "pipes");

        Assert.Single(result);
    }

    [Fact]
    public void FilterNodesWithAncestors_PartialMatch()
    {
        var nodes = new List<CategoryNode>
        {
            MakeNode("1", "Steel Pipes"),
        };
        var tree = new CategoryTree(nodes);

        var result = CategoryPickerViewModel.FilterNodesWithAncestors(tree, "tee");

        Assert.Single(result);
    }

    [Fact]
    public void FilterNodesWithAncestors_MultipleMatches()
    {
        var nodes = new List<CategoryNode>
        {
            MakeNode("1", "Pipes"),
            MakeNode("2", "Pipe Fittings", "1"),
            MakeNode("3", "Valves", "1"),
        };
        var tree = new CategoryTree(nodes);

        var result = CategoryPickerViewModel.FilterNodesWithAncestors(tree, "pipe");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.Id == "1");
        Assert.Contains(result, n => n.Id == "2");
    }
}
