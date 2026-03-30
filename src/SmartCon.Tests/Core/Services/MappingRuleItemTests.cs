using SmartCon.Core.Models;
using SmartCon.PipeConnect.ViewModels;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class MappingRuleItemTests
{
    // ── From ──────────────────────────────────────────────────────────────

    [Fact]
    public void From_CopiesTypeCodes()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType   = new ConnectionTypeCode(2),
        };
        var item = MappingRuleItem.From(rule);
        Assert.Equal(1, item.FromTypeCode);
        Assert.Equal(2, item.ToTypeCode);
    }

    [Fact]
    public void From_CopiesIsDirectConnect()
    {
        var item = MappingRuleItem.From(new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType   = new ConnectionTypeCode(1),
            IsDirectConnect = true,
        });
        Assert.True(item.IsDirectConnect);
    }

    [Fact]
    public void From_SingleFamily_PopulatesCollection()
    {
        var item = MappingRuleItem.From(new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType   = new ConnectionTypeCode(2),
            FittingFamilies = [new FittingMapping { FamilyName = "Переходник", Priority = 1 }],
        });
        Assert.Single(item.FittingFamilies);
        Assert.Equal("Переходник", item.FittingFamilies[0].FamilyName);
    }

    [Fact]
    public void From_MultipleFamilies_PopulatesCollectionInPriorityOrder()
    {
        var item = MappingRuleItem.From(new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType   = new ConnectionTypeCode(2),
            FittingFamilies =
            [
                new FittingMapping { FamilyName = "А", Priority = 1 },
                new FittingMapping { FamilyName = "Б", Priority = 2 },
            ],
        });
        Assert.Equal(2, item.FittingFamilies.Count);
        Assert.Equal("А", item.FittingFamilies[0].FamilyName);
        Assert.Equal("Б", item.FittingFamilies[1].FamilyName);
    }

    [Fact]
    public void From_NoFamilies_EmptyCollection()
    {
        var item = MappingRuleItem.From(new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType   = new ConnectionTypeCode(2),
        });
        Assert.Empty(item.FittingFamilies);
    }

    [Fact]
    public void FamiliesDisplayText_SingleFamily_ShowsPriorityAndName()
    {
        var item = new MappingRuleItem();
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "Муфта", Priority = 1 });

        Assert.Equal("1.Муфта", item.FamiliesDisplayText);
    }

    [Fact]
    public void FamiliesDisplayText_MultipleFamilies_ShowsCommaSeparated()
    {
        var item = new MappingRuleItem();
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "Муфта", Priority = 1 });
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "Переходник", Priority = 2 });

        Assert.Equal("1.Муфта, 2.Переходник", item.FamiliesDisplayText);
    }

    [Fact]
    public void FamiliesDisplayText_NoFamilies_ShowsNotSet()
    {
        var item = new MappingRuleItem();
        Assert.Equal("(не задано)", item.FamiliesDisplayText);
    }

    // ── ToRule ────────────────────────────────────────────────────────────

    [Fact]
    public void ToRule_RoundTrip_TypeCodes()
    {
        var item = new MappingRuleItem { FromTypeCode = 3, ToTypeCode = 5 };
        var rule = item.ToRule();
        Assert.Equal(3, rule.FromType.Value);
        Assert.Equal(5, rule.ToType.Value);
    }

    [Fact]
    public void ToRule_RoundTrip_IsDirectConnect()
    {
        var item = new MappingRuleItem { IsDirectConnect = true };
        Assert.True(item.ToRule().IsDirectConnect);
    }

    [Fact]
    public void ToRule_SingleFamily_OneFitting()
    {
        var item = new MappingRuleItem();
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "Переходник", Priority = 1 });

        var rule = item.ToRule();
        Assert.Single(rule.FittingFamilies);
        Assert.Equal("Переходник", rule.FittingFamilies[0].FamilyName);
    }

    [Fact]
    public void ToRule_MultipleFamilies_PreservesPriority()
    {
        var item = new MappingRuleItem();
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "А", Priority = 1 });
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "Б", Priority = 2 });
        item.FittingFamilies.Add(new FittingMapping { FamilyName = "В", Priority = 3 });

        var rule = item.ToRule();
        Assert.Equal(3, rule.FittingFamilies.Count);
        Assert.Equal("А", rule.FittingFamilies[0].FamilyName);
        Assert.Equal(1, rule.FittingFamilies[0].Priority);
        Assert.Equal("Б", rule.FittingFamilies[1].FamilyName);
        Assert.Equal(2, rule.FittingFamilies[1].Priority);
    }

    [Fact]
    public void ToRule_NoFamilies_EmptyList()
    {
        var item = new MappingRuleItem();
        var rule = item.ToRule();
        Assert.Empty(rule.FittingFamilies);
    }

    // ── Round-trip ────────────────────────────────────────────────────────

    [Fact]
    public void FullRoundTrip_PreservesAllData()
    {
        var originalRule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(2),
            IsDirectConnect = true,
            FittingFamilies =
            [
                new FittingMapping { FamilyName = "СварнойШов", SymbolName = "DN50", Priority = 1 },
                new FittingMapping { FamilyName = "Переходник_С-Р", SymbolName = "*", Priority = 2 },
            ]
        };

        var item = MappingRuleItem.From(originalRule);
        var resultRule = item.ToRule();

        Assert.Equal(originalRule.FromType.Value, resultRule.FromType.Value);
        Assert.Equal(originalRule.ToType.Value, resultRule.ToType.Value);
        Assert.Equal(originalRule.IsDirectConnect, resultRule.IsDirectConnect);
        Assert.Equal(2, resultRule.FittingFamilies.Count);
        Assert.Equal("СварнойШов", resultRule.FittingFamilies[0].FamilyName);
        Assert.Equal("DN50", resultRule.FittingFamilies[0].SymbolName);
        Assert.Equal(1, resultRule.FittingFamilies[0].Priority);
    }
}
