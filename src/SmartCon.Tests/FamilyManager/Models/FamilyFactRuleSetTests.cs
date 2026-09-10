using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

/// <summary>
/// Tests for <see cref="FamilyFactRuleSet"/> (ADR-055): the static registry
/// that drives extraction, migration detection and the properties window.
/// </summary>
public sealed class FamilyFactRuleSetTests
{
    [Fact]
    public void GetRulesForCategory_PipeFitting_ReturnsPartTypeAndConnectorShapeRules()
    {
        var rules = FamilyFactRuleSet.GetRulesForCategory(-2008049); // OST_PipeFitting

        Assert.Equal(2, rules.Count);
        var partType = Assert.Single(rules, r => r.FactKey == FamilyFactRuleSet.PartTypeFactKey);
        Assert.Equal(FamilyFactRuleSet.PartTypeLabelKey, partType.LabelKey);
        Assert.Equal(-1114206, partType.ParameterId); // FAMILY_CONTENT_PART_TYPE
        // Owner stress test 2026-09-01 (баг 8): the connector-shape fact is
        // computed from the family's connectors, not read from a parameter.
        var shape = Assert.Single(rules, r => r.FactKey == FamilyFactRuleSet.ConnectorShapeFactKey);
        Assert.Equal(FamilyFactRuleSet.ConnectorShapeLabelKey, shape.LabelKey);
        Assert.Equal(FamilyFactRuleSet.ComputedFactParameterId, shape.ParameterId);
    }

    [Theory]
    [InlineData(-2008049)] // OST_PipeFitting
    [InlineData(-2008010)] // OST_DuctFitting
    [InlineData(-2008126)] // OST_CableTrayFitting
    [InlineData(-2008128)] // OST_ConduitFitting
    public void GetRulesForCategory_AllFittingCategories_HavePartTypeRule(int categoryId)
    {
        var rules = FamilyFactRuleSet.GetRulesForCategory(categoryId);

        Assert.Contains(rules, r => r.FactKey == FamilyFactRuleSet.PartTypeFactKey);
    }

    [Theory]
    [InlineData(-2008055)] // OST_PipeAccessory — исключены продуктовым решением (только фитинги)
    [InlineData(-2008016)] // OST_DuctAccessory
    public void GetRulesForCategory_Accessories_HaveNoRules(int categoryId)
    {
        Assert.Empty(FamilyFactRuleSet.GetRulesForCategory(categoryId));
    }

    [Fact]
    public void GetRulesForCategory_CategoryWithoutRules_ReturnsEmpty()
    {
        Assert.Empty(FamilyFactRuleSet.GetRulesForCategory(-2008044)); // OST_PipeCurves
    }

    [Fact]
    public void CategoryIdsWithRules_MatchesRuleCategories_Distinct()
    {
        var fromRules = FamilyFactRuleSet.Rules.Select(r => r.CategoryId).Distinct().OrderBy(x => x).ToArray();
        var fromProperty = FamilyFactRuleSet.CategoryIdsWithRules.OrderBy(x => x).ToArray();

        Assert.Equal(fromRules, fromProperty);
    }

    [Fact]
    public void FindRule_Hit_ReturnsRule()
    {
        var rule = FamilyFactRuleSet.FindRule(-2008049, FamilyFactRuleSet.PartTypeFactKey);

        Assert.NotNull(rule);
    }

    [Fact]
    public void FindRule_WrongKey_ReturnsNull()
    {
        Assert.Null(FamilyFactRuleSet.FindRule(-2008049, "no_such_fact"));
    }

    [Fact]
    public void FindRule_WrongCategory_ReturnsNull()
    {
        Assert.Null(FamilyFactRuleSet.FindRule(-2008044, FamilyFactRuleSet.PartTypeFactKey));
    }
}
