using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Nearest rule-bearing ancestor resolution for the «Взять условия
/// родителя» button (#241): walks the tree's Parent chain and stops at
/// the first node with configured rules.
/// </summary>
public sealed class NearestAncestorWithRulesTests
{
    private static CategoryNodeViewModel Node(string id, string? parentId) =>
        new(id, id, parentId, id);

    [Fact]
    public void WalksUp_ToFirstAncestorWithRules()
    {
        var a = Node("a", null);
        var b = Node("b", "a");
        var c = Node("c", "b");
        a.Children.Add(b);   // Parent pointers are wired by Children.Add
        b.Children.Add(c);
        a.AssignmentRuleCount = 2;

        var result = CategoryTreeEditorViewModel.FindNearestAncestorWithRules(c);

        Assert.NotNull(result);
        Assert.Equal("a", result.Value.CategoryId);
    }

    [Fact]
    public void Stops_AtNearest_NotTheRoot()
    {
        var a = Node("a", null);
        var b = Node("b", "a");
        var c = Node("c", "b");
        a.Children.Add(b);
        b.Children.Add(c);
        a.AssignmentRuleCount = 2;
        b.AssignmentRuleCount = 1;

        var result = CategoryTreeEditorViewModel.FindNearestAncestorWithRules(c);

        Assert.Equal("b", result!.Value.CategoryId);
    }

    [Fact]
    public void Skips_AncestorWithoutRules_ContinuesUp()
    {
        var a = Node("a", null);
        var b = Node("b", "a");
        var c = Node("c", "b");
        a.Children.Add(b);
        b.Children.Add(c);
        a.AssignmentRuleCount = 2; // b has no rules

        var result = CategoryTreeEditorViewModel.FindNearestAncestorWithRules(c);

        Assert.Equal("a", result!.Value.CategoryId);
    }

    [Fact]
    public void NoAncestorWithRules_ReturnsNull()
    {
        var a = Node("a", null);
        var b = Node("b", "a");
        a.Children.Add(b);

        var result = CategoryTreeEditorViewModel.FindNearestAncestorWithRules(b);

        Assert.Null(result);
    }

    [Fact]
    public void RootNode_ReturnsNull()
    {
        var a = Node("a", null);
        a.AssignmentRuleCount = 3;

        var result = CategoryTreeEditorViewModel.FindNearestAncestorWithRules(a);

        Assert.Null(result);
    }
}
