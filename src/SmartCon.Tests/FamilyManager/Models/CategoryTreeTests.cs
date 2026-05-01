using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class CategoryTreeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static CategoryNode Node(string id, string name, string? parentId = null, string? fullPath = null) =>
        new(id, name, parentId, 0, fullPath ?? name, Now);

    [Fact]
    public void Constructor_EmptyList_NoNodes()
    {
        var tree = new CategoryTree([]);

        Assert.Empty(tree.GetAllNodes());
        Assert.Empty(tree.GetRootNodes());
    }

    [Fact]
    public void GetAllNodes_ReturnsAllNodes()
    {
        var nodes = new[]
        {
            Node("1", "A"),
            Node("2", "B"),
            Node("3", "C")
        };

        var tree = new CategoryTree(nodes);

        Assert.Equal(3, tree.GetAllNodes().Count);
    }

    [Fact]
    public void GetById_ExistingId_ReturnsNode()
    {
        var nodes = new[] { Node("1", "Pipes"), Node("2", "Fittings") };
        var tree = new CategoryTree(nodes);

        var result = tree.GetById("1");

        Assert.NotNull(result);
        Assert.Equal("Pipes", result.Name);
    }

    [Fact]
    public void GetById_UnknownId_ReturnsNull()
    {
        var tree = new CategoryTree([Node("1", "A")]);

        Assert.Null(tree.GetById("999"));
    }

    [Fact]
    public void GetChildren_ReturnsDirectChildren()
    {
        var nodes = new[]
        {
            Node("1", "Root"),
            Node("2", "Child1", "1"),
            Node("3", "Child2", "1"),
            Node("4", "OtherRoot")
        };
        var tree = new CategoryTree(nodes);

        var children = tree.GetChildren("1");

        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal("1", c.ParentId));
    }

    [Fact]
    public void GetChildren_NoChildren_ReturnsEmpty()
    {
        var tree = new CategoryTree([Node("1", "Leaf")]);

        Assert.Empty(tree.GetChildren("1"));
    }

    [Fact]
    public void GetRootNodes_ReturnsOnlyRootNodes()
    {
        var nodes = new[]
        {
            Node("1", "Root1"),
            Node("2", "Root2"),
            Node("3", "Child", "1")
        };
        var tree = new CategoryTree(nodes);

        var roots = tree.GetRootNodes();

        Assert.Equal(2, roots.Count);
        Assert.All(roots, r => Assert.Null(r.ParentId));
    }

    [Fact]
    public void GetDescendantIds_ReturnsAllDescendantsRecursively()
    {
        var nodes = new[]
        {
            Node("1", "Root"),
            Node("2", "Child", "1"),
            Node("3", "Grandchild", "2"),
            Node("4", "GreatGrandchild", "3")
        };
        var tree = new CategoryTree(nodes);

        var descendants = tree.GetDescendantIds("1");

        Assert.Equal(["2", "3", "4"], descendants);
    }

    [Fact]
    public void GetDescendantIds_LeafNode_ReturnsEmpty()
    {
        var tree = new CategoryTree([Node("1", "Leaf")]);

        Assert.Empty(tree.GetDescendantIds("1"));
    }

    [Fact]
    public void GetDescendantIds_UnknownId_ReturnsEmpty()
    {
        var tree = new CategoryTree([Node("1", "A")]);

        Assert.Empty(tree.GetDescendantIds("999"));
    }

    [Fact]
    public void BuildFullPath_SingleNode_ReturnsName()
    {
        var tree = new CategoryTree([Node("1", "Root")]);

        Assert.Equal("Root", tree.BuildFullPath("1"));
    }

    [Fact]
    public void BuildFullPath_DeepNesting_ReturnsJoinedPath()
    {
        var nodes = new[]
        {
            Node("1", "MEP"),
            Node("2", "Pipes", "1"),
            Node("3", "Steel Pipes", "2")
        };
        var tree = new CategoryTree(nodes);

        Assert.Equal("MEP > Pipes > Steel Pipes", tree.BuildFullPath("3"));
    }

    [Fact]
    public void BuildFullPath_TwoLevels_ReturnsParentAndChild()
    {
        var nodes = new[]
        {
            Node("1", "HVAC"),
            Node("2", "Ducts", "1")
        };
        var tree = new CategoryTree(nodes);

        Assert.Equal("HVAC > Ducts", tree.BuildFullPath("2"));
    }

    [Fact]
    public void BuildFullPath_UnknownId_ReturnsEmpty()
    {
        var tree = new CategoryTree([Node("1", "A")]);

        Assert.Equal(string.Empty, tree.BuildFullPath("999"));
    }

    [Fact]
    public void GetFullPath_ReturnsPrecomputedPath()
    {
        var nodes = new[] { Node("1", "A", fullPath: "MEP > A") };
        var tree = new CategoryTree(nodes);

        Assert.Equal("MEP > A", tree.GetFullPath("1"));
    }

    [Fact]
    public void GetFullPath_UnknownId_ReturnsEmpty()
    {
        var tree = new CategoryTree([Node("1", "A")]);

        Assert.Equal(string.Empty, tree.GetFullPath("999"));
    }

    [Fact]
    public void GetChildren_NullParentId_ReturnsRootNodes()
    {
        var nodes = new[]
        {
            Node("1", "Root"),
            Node("2", "Child", "1")
        };
        var tree = new CategoryTree(nodes);

        var result = tree.GetChildren(null);

        Assert.Single(result);
        Assert.Equal("Root", result[0].Name);
    }

    [Fact]
    public void SingleNode_IsRootAndHasNoChildren()
    {
        var tree = new CategoryTree([Node("1", "Solo")]);

        Assert.Single(tree.GetRootNodes());
        Assert.Empty(tree.GetChildren("1"));
        Assert.Equal("Solo", tree.BuildFullPath("1"));
    }
}
