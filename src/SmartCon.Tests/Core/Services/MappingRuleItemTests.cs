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
    public void From_SingleFamily_CsvIsName()
    {
        var item = MappingRuleItem.From(new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType   = new ConnectionTypeCode(2),
            FittingFamilies = [new FittingMapping { FamilyName = "Переходник", Priority = 1 }],
        });
        Assert.Equal("Переходник", item.FittingFamiliesCsv);
    }

    [Fact]
    public void From_MultipleFamilies_JoinedWithComma()
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
        Assert.Equal("А, Б", item.FittingFamiliesCsv);
    }

    [Fact]
    public void From_NoFamilies_EmptyCsv()
    {
        var item = MappingRuleItem.From(new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType   = new ConnectionTypeCode(2),
        });
        Assert.Equal(string.Empty, item.FittingFamiliesCsv);
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
    public void ToRule_CsvSingleFamily_OneFitting()
    {
        var item = new MappingRuleItem { FittingFamiliesCsv = "Переходник" };
        var rule = item.ToRule();
        Assert.Single(rule.FittingFamilies);
        Assert.Equal("Переходник", rule.FittingFamilies[0].FamilyName);
    }

    [Fact]
    public void ToRule_CsvMultipleFamilies_AssignsPriorityByOrder()
    {
        var item = new MappingRuleItem { FittingFamiliesCsv = "А, Б, В" };
        var rule = item.ToRule();
        Assert.Equal(3, rule.FittingFamilies.Count);
        Assert.Equal(1, rule.FittingFamilies[0].Priority);
        Assert.Equal(2, rule.FittingFamilies[1].Priority);
        Assert.Equal(3, rule.FittingFamilies[2].Priority);
    }

    [Fact]
    public void ToRule_CsvWithSpaces_Trimmed()
    {
        var item = new MappingRuleItem { FittingFamiliesCsv = "  Муфта  ,  Переходник  " };
        var rule = item.ToRule();
        Assert.Equal("Муфта", rule.FittingFamilies[0].FamilyName);
        Assert.Equal("Переходник", rule.FittingFamilies[1].FamilyName);
    }

    [Fact]
    public void ToRule_EmptyCsv_NoFamilies()
    {
        var item = new MappingRuleItem { FittingFamiliesCsv = "" };
        var rule = item.ToRule();
        Assert.Empty(rule.FittingFamilies);
    }

    [Fact]
    public void ToRule_OnlyCommas_NoFamilies()
    {
        var item = new MappingRuleItem { FittingFamiliesCsv = ",  ,  ," };
        var rule = item.ToRule();
        Assert.Empty(rule.FittingFamilies);
    }

    [Fact]
    public void PropertyChanged_RaisedOnFittingFamiliesCsvChange()
    {
        var item = new MappingRuleItem();
        var changed = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.FittingFamiliesCsv)) changed = true;
        };
        item.FittingFamiliesCsv = "Новое";
        Assert.True(changed);
    }
}
