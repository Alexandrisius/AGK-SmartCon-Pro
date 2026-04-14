using SmartCon.Core.Models;
using Xunit;
using SmartCon.Core;

using static SmartCon.Core.Units;
namespace SmartCon.Tests.Core.Models;

public sealed class FamilySizeOptionTests
{
    private static double DnToRadius(int dn) => (dn / 2.0) * MmToFeet;

    [Fact]
    public void RequiredProperties_SetCorrectly()
    {
        var opt = new FamilySizeOption
        {
            DisplayName = "DN 50 × DN 32",
            Radius = DnToRadius(50),
            TargetConnectorIndex = 0,
            AllConnectorRadii = new Dictionary<int, double>
            {
                [0] = DnToRadius(50),
                [1] = DnToRadius(32)
            },
            Source = "LookupTable"
        };

        Assert.Equal("DN 50 × DN 32", opt.DisplayName);
        Assert.Equal(DnToRadius(50), opt.Radius, 8);
        Assert.Equal(0, opt.TargetConnectorIndex);
        Assert.Equal(2, opt.AllConnectorRadii.Count);
        Assert.Equal(DnToRadius(50), opt.AllConnectorRadii[0], 8);
        Assert.Equal(DnToRadius(32), opt.AllConnectorRadii[1], 8);
        Assert.False(opt.IsAutoSelect);
    }

    [Fact]
    public void AutoSelect_HasFlagAndEmptySource()
    {
        var opt = new FamilySizeOption
        {
            DisplayName = "АВТОПОДБОР (DN 50 × DN 32)",
            Radius = DnToRadius(50),
            TargetConnectorIndex = 0,
            Source = "",
            IsAutoSelect = true
        };

        Assert.True(opt.IsAutoSelect);
        Assert.Empty(opt.Source);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var opt = new FamilySizeOption
        {
            DisplayName = "DN 50",
            Radius = DnToRadius(50),
            TargetConnectorIndex = 0
        };

        Assert.Empty(opt.AllConnectorRadii);
        Assert.Equal("FamilySymbol", opt.Source);
        Assert.False(opt.IsAutoSelect);
        Assert.Null(opt.SymbolName);
        Assert.Null(opt.CurrentSymbolName);
        Assert.Empty(opt.NonSizeParameterValues);
        Assert.False(opt.RequiresNonSizeParameterChange);
    }

    [Fact]
    public void With_CreatesModifiedCopy()
    {
        var original = new FamilySizeOption
        {
            DisplayName = "DN 50",
            Radius = DnToRadius(50),
            TargetConnectorIndex = 0,
            Source = "LookupTable",
            IsAutoSelect = false
        };

        var modified = original with { IsAutoSelect = true, Source = "" };

        Assert.False(original.IsAutoSelect);
        Assert.Equal("LookupTable", original.Source);
        Assert.True(modified.IsAutoSelect);
        Assert.Empty(modified.Source);
        Assert.Equal("DN 50", modified.DisplayName);
    }

    [Fact]
    public void SymbolNames_TrackChange()
    {
        var opt = new FamilySizeOption
        {
            DisplayName = "DN 25",
            Radius = DnToRadius(25),
            TargetConnectorIndex = 0,
            SymbolName = "ТИП_2",
            CurrentSymbolName = "ТИП_1"
        };

        Assert.Equal("ТИП_2", opt.SymbolName);
        Assert.Equal("ТИП_1", opt.CurrentSymbolName);
        Assert.NotEqual(opt.SymbolName, opt.CurrentSymbolName);
    }

    [Fact]
    public void ToString_ReturnsDisplayName()
    {
        var opt = new FamilySizeOption
        {
            DisplayName = "DN 65 × DN 50 × DN 65",
            Radius = DnToRadius(65),
            TargetConnectorIndex = 0
        };

        Assert.Equal("DN 65 × DN 50 × DN 65", opt.ToString());
    }

    [Fact]
    public void NonSizeParameterValues_CanStoreParameters()
    {
        var opt = new FamilySizeOption
        {
            DisplayName = "DN 50",
            Radius = DnToRadius(50),
            TargetConnectorIndex = 0,
            NonSizeParameterValues = new Dictionary<string, string>
            {
                ["ТИП_ОРГАНА"] = "Ручка"
            },
            RequiresNonSizeParameterChange = true
        };

        Assert.True(opt.RequiresNonSizeParameterChange);
        Assert.Equal("Ручка", opt.NonSizeParameterValues["ТИП_ОРГАНА"]);
    }
}
