using SmartCon.Core.Models;
using SmartCon.Core.Services.Implementation;
using SmartCon.PipeConnect.ViewModels;
using Xunit;

namespace SmartCon.Tests.Core.Models;

/// <summary>
/// Тесты FittingCardItem: отображение, редьюсеры, прямое соединение.
/// </summary>
public sealed class FittingCardItemTests
{
    [Fact]
    public void DirectConnectRule_ShowsDirectConnectMessage()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(1),
            IsDirectConnect = true,
        };

        var item = new FittingCardItem(rule);

        Assert.True(item.IsDirectConnect);
        Assert.False(item.IsReducer);
        Assert.Equal("Без фитинга (прямое соединение)", item.DisplayName);
        Assert.Null(item.PrimaryFitting);
    }

    [Fact]
    public void RuleWithFitting_ShowsFamilyName()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(2),
            IsDirectConnect = false,
            FittingFamilies =
            [
                new FittingMapping { FamilyName = "Муфта", SymbolName = "*", Priority = 1 },
            ],
        };

        var item = new FittingCardItem(rule, rule.FittingFamilies[0]);

        Assert.False(item.IsDirectConnect);
        Assert.False(item.IsReducer);
        Assert.Equal("Муфта", item.DisplayName);
        Assert.Equal("Муфта", item.PrimaryFitting?.FamilyName);
    }

    [Fact]
    public void RuleWithFittingAndSymbol_ShowsFamilyAndSymbol()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(1),
            IsDirectConnect = false,
            FittingFamilies =
            [
                new FittingMapping { FamilyName = "Отвод", SymbolName = "DN50", Priority = 1 },
            ],
        };

        var item = new FittingCardItem(rule, rule.FittingFamilies[0]);

        Assert.Equal("Отвод — DN50", item.DisplayName);
    }

    [Fact]
    public void ReducerFitting_ShowsReducerPrefix()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(1),
            IsDirectConnect = false,
            ReducerFamilies =
            [
                new FittingMapping { FamilyName = "Переход", SymbolName = "DN50-DN25", Priority = 1 },
            ],
        };

        var item = new FittingCardItem(rule, rule.ReducerFamilies[0], isReducer: true);

        Assert.False(item.IsDirectConnect);
        Assert.True(item.IsReducer);
        Assert.Equal("🔧 Переход — DN50-DN25 (переход)", item.DisplayName);
    }

    [Fact]
    public void ReducerFittingWithoutSymbol_ShowsOnlyFamilyName()
    {
        var reducer = new FittingMapping { FamilyName = "Ниппель", SymbolName = "*", Priority = 1 };
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(2),
            ToType = new ConnectionTypeCode(2),
            IsDirectConnect = false,
        };

        var item = new FittingCardItem(rule, reducer, isReducer: true);

        Assert.True(item.IsReducer);
        Assert.Equal("🔧 Ниппель (переход)", item.DisplayName);
    }

    [Fact]
    public void MultipleFittings_CreateSeparateItems()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(1),
            IsDirectConnect = false,
            FittingFamilies =
            [
                new FittingMapping { FamilyName = "Муфта1", SymbolName = "*", Priority = 1 },
                new FittingMapping { FamilyName = "Муфта2", SymbolName = "*", Priority = 2 },
            ],
        };

        var items = rule.FittingFamilies
            .OrderBy(f => f.Priority)
            .Select(f => new FittingCardItem(rule, f))
            .ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal("Муфта1", items[0].DisplayName);
        Assert.Equal("Муфта2", items[1].DisplayName);
        Assert.All(items, i => Assert.False(i.IsReducer));
    }

    [Fact]
    public void RuleWithoutFitting_ShowsTypeCodes()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(2),
            IsDirectConnect = false,
        };

        var item = new FittingCardItem(rule);

        Assert.False(item.IsDirectConnect);
        Assert.Equal("Тип 1 → 2", item.DisplayName);
    }

    [Fact]
    public void DirectConnectWithEmptyFamilies_IsDirectConnect()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(1),
            IsDirectConnect = true,
            FittingFamilies = [],
        };

        var item = new FittingCardItem(rule);

        Assert.True(item.IsDirectConnect);
    }

    [Fact]
    public void DirectConnectWithFitting_IsNotDirectConnect()
    {
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(1),
            IsDirectConnect = true,
            FittingFamilies =
            [
                new FittingMapping { FamilyName = "Муфта", SymbolName = "*", Priority = 1 },
            ],
        };

        var item = new FittingCardItem(rule, rule.FittingFamilies[0]);

        Assert.False(item.IsDirectConnect);
        Assert.False(item.IsReducer);
    }

    [Fact]
    public void ToString_ReturnsDisplayName()
    {
        var reducer = new FittingMapping { FamilyName = "Переходник", SymbolName = "*", Priority = 1 };
        var rule = new FittingMappingRule
        {
            FromType = new ConnectionTypeCode(1),
            ToType = new ConnectionTypeCode(1),
            IsDirectConnect = false,
        };

        var item = new FittingCardItem(rule, reducer, isReducer: true);

        Assert.Equal(item.DisplayName, item.ToString());
    }
}
