using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

public sealed class FamilySizeFormatterTests
{
    private static double Dn(int dn) => FamilySizeFormatter.DnToRadiusFt(dn);

    [Fact]
    public void BuildDisplayName_Empty_ReturnsPlaceholder()
    {
        var radii = Array.Empty<double>();

        var result = FamilySizeFormatter.BuildDisplayName(radii, 1);

        Assert.Equal("DN ?", result);
    }

    [Fact]
    public void BuildDisplayName_SingleParam_ReturnsSimpleDn()
    {
        var radii = new[] { Dn(50) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 1);

        Assert.Equal("DN 50", result);
    }

    [Fact]
    public void BuildDisplayName_TwoParamsSameDn_ShowsBoth()
    {
        var radii = new[] { Dn(50), Dn(50) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 1);

        Assert.Equal("DN 50 × DN 50", result);
    }

    [Fact]
    public void BuildDisplayName_TwoParamsDifferentDn_TargetFirst()
    {
        var radii = new[] { Dn(20), Dn(25) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 1);

        Assert.Equal("DN 20 × DN 25", result);
    }

    [Fact]
    public void BuildDisplayName_TwoParams_TargetIsSecond()
    {
        var radii = new[] { Dn(20), Dn(25) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 2);

        Assert.Equal("DN 25 × DN 20", result);
    }

    [Fact]
    public void BuildDisplayName_TwoParams_TargetSameAsFirst()
    {
        var radii = new[] { Dn(65), Dn(50) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 1);

        Assert.Equal("DN 65 × DN 50", result);
    }

    [Fact]
    public void BuildDisplayName_TwoParams_TargetSameAsSecond()
    {
        var radii = new[] { Dn(65), Dn(50) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 2);

        Assert.Equal("DN 50 × DN 65", result);
    }

    [Fact]
    public void BuildDisplayName_ThreeParams_TargetFirst()
    {
        var radii = new[] { Dn(65), Dn(50), Dn(50) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 1);

        Assert.Equal("DN 65 × DN 50 × DN 50", result);
    }

    [Fact]
    public void BuildDisplayName_ThreeParams_TargetSecond()
    {
        var radii = new[] { Dn(65), Dn(50), Dn(32) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 2);

        Assert.Equal("DN 50 × DN 65 × DN 32", result);
    }

    [Fact]
    public void BuildDisplayName_ThreeParams_TargetThird()
    {
        var radii = new[] { Dn(65), Dn(50), Dn(32) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 3);

        Assert.Equal("DN 32 × DN 65 × DN 50", result);
    }

    [Fact]
    public void BuildDisplayName_ThreeParamsAllSame_ShowsAll()
    {
        var radii = new[] { Dn(50), Dn(50), Dn(50) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 1);

        Assert.Equal("DN 50 × DN 50 × DN 50", result);
    }

    [Fact]
    public void BuildDisplayName_FourParams_Cross()
    {
        var radii = new[] { Dn(65), Dn(65), Dn(65), Dn(50) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 4);

        Assert.Equal("DN 50 × DN 65 × DN 65 × DN 65", result);
    }

    [Fact]
    public void BuildDisplayName_TargetIndexOutOfRange_FallbackToFirst()
    {
        var radii = new[] { Dn(50), Dn(32) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 99);

        Assert.Equal("DN 50 × DN 32", result);
    }

    [Fact]
    public void BuildDisplayName_TargetIndexZero_FallbackToFirst()
    {
        var radii = new[] { Dn(50), Dn(32) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 0);

        Assert.Equal("DN 50 × DN 32", result);
    }

    [Fact]
    public void BuildDisplayName_LargeDn()
    {
        var radii = new[] { Dn(300) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 1);

        Assert.Equal("DN 300", result);
    }

    [Fact]
    public void BuildDisplayName_Tee_Dn65x50_TargetBranch()
    {
        var radii = new[] { Dn(65), Dn(50) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 2);

        Assert.Equal("DN 50 × DN 65", result);
    }

    [Fact]
    public void BuildDisplayName_Valve_OneParam_TwoConnectors()
    {
        var radii = new[] { Dn(50) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 1);

        Assert.Equal("DN 50", result);
    }

    [Fact]
    public void BuildDisplayName_Nipple_TwoParams()
    {
        var radii = new[] { Dn(50), Dn(32) };

        var result = FamilySizeFormatter.BuildDisplayName(radii, 1);

        Assert.Equal("DN 50 × DN 32", result);
    }

    [Fact]
    public void BuildDisplayNameLegacy_SingleConnector_ReturnsSimpleDn()
    {
        var radii = new Dictionary<int, double> { [0] = Dn(50) };

        var result = FamilySizeFormatter.BuildDisplayNameLegacy(radii, 0);

        Assert.Equal("DN 50", result);
    }

    [Fact]
    public void BuildDisplayNameLegacy_TwoConnectorsSameDn_TargetFirst()
    {
        var radii = new Dictionary<int, double>
        {
            [0] = Dn(50),
            [1] = Dn(50)
        };

        var result = FamilySizeFormatter.BuildDisplayNameLegacy(radii, 0);

        Assert.Equal("DN 50 × DN 50", result);
    }

    [Fact]
    public void BuildDisplayNameLegacy_TwoConnectorsDifferentDn_TargetFirst()
    {
        var radii = new Dictionary<int, double>
        {
            [0] = Dn(20),
            [1] = Dn(25)
        };

        var result = FamilySizeFormatter.BuildDisplayNameLegacy(radii, 0);

        Assert.Equal("DN 20 × DN 25", result);
    }

    [Fact]
    public void BuildDisplayNameLegacy_TargetNotInRadii_FallbackToOrdered()
    {
        var radii = new Dictionary<int, double>
        {
            [0] = Dn(50),
            [1] = Dn(32)
        };

        var result = FamilySizeFormatter.BuildDisplayNameLegacy(radii, 99);

        Assert.Equal("DN 50 × DN 32", result);
    }

    [Fact]
    public void BuildAutoSelectDisplayName_Empty()
    {
        var radii = Array.Empty<double>();

        var result = FamilySizeFormatter.BuildAutoSelectDisplayName(radii, 1);

        Assert.Equal("АВТОПОДБОР (DN 0)", result);
    }

    [Fact]
    public void BuildAutoSelectDisplayName_SingleParam()
    {
        var radii = new[] { Dn(50) };

        var result = FamilySizeFormatter.BuildAutoSelectDisplayName(radii, 1);

        Assert.Equal("АВТОПОДБОР (DN 50)", result);
    }

    [Fact]
    public void BuildAutoSelectDisplayName_TwoParamsSameDn()
    {
        var radii = new[] { Dn(50), Dn(50) };

        var result = FamilySizeFormatter.BuildAutoSelectDisplayName(radii, 1);

        Assert.Equal("АВТОПОДБОР (DN 50 × DN 50)", result);
    }

    [Fact]
    public void BuildAutoSelectDisplayName_TwoParamsDifferentDn()
    {
        var radii = new[] { Dn(25), Dn(20) };

        var result = FamilySizeFormatter.BuildAutoSelectDisplayName(radii, 1);

        Assert.Equal("АВТОПОДБОР (DN 25 × DN 20)", result);
    }

    [Fact]
    public void BuildAutoSelectDisplayName_ThreeParams()
    {
        var radii = new[] { Dn(65), Dn(65), Dn(50) };

        var result = FamilySizeFormatter.BuildAutoSelectDisplayName(radii, 2);

        Assert.Equal("АВТОПОДБОР (DN 65 × DN 65 × DN 50)", result);
    }

    [Fact]
    public void BuildAutoSelectDisplayName_TargetOutOfRange_FallbackToFirst()
    {
        var radii = new[] { Dn(50), Dn(32) };

        var result = FamilySizeFormatter.BuildAutoSelectDisplayName(radii, 0);

        Assert.Equal("АВТОПОДБОР (DN 50 × DN 32)", result);
    }

    [Fact]
    public void BuildAutoSelectDisplayName_WithSymbolName()
    {
        var radii = new[] { Dn(50) };

        var result = FamilySizeFormatter.BuildAutoSelectDisplayName(radii, 1, "Исполнение 2");

        Assert.Equal("АВТОПОДБОР (DN 50) (Исполнение 2)", result);
    }

    [Fact]
    public void BuildAutoSelectDisplayName_WithNullSymbolName_NoSuffix()
    {
        var radii = new[] { Dn(50) };

        var result = FamilySizeFormatter.BuildAutoSelectDisplayName(radii, 1, null);

        Assert.Equal("АВТОПОДБОР (DN 50)", result);
    }

    [Fact]
    public void BuildAutoSelectDisplayName_WithEmptySymbolName_NoSuffix()
    {
        var radii = new[] { Dn(50) };

        var result = FamilySizeFormatter.BuildAutoSelectDisplayName(radii, 1, "");

        Assert.Equal("АВТОПОДБОР (DN 50)", result);
    }

    [Fact]
    public void ToDn_RoundTrip()
    {
        foreach (var dn in new[] { 15, 20, 25, 32, 40, 50, 65, 80, 100, 150, 200, 300 })
        {
            var radius = FamilySizeFormatter.DnToRadiusFt(dn);
            var recovered = FamilySizeFormatter.ToDn(radius);
            Assert.Equal(dn, recovered);
        }
    }

    [Fact]
    public void ToDn_AllStandardSizes()
    {
        Assert.Equal(15, FamilySizeFormatter.ToDn(FamilySizeFormatter.DnToRadiusFt(15)));
        Assert.Equal(25, FamilySizeFormatter.ToDn(FamilySizeFormatter.DnToRadiusFt(25)));
        Assert.Equal(32, FamilySizeFormatter.ToDn(FamilySizeFormatter.DnToRadiusFt(32)));
        Assert.Equal(50, FamilySizeFormatter.ToDn(FamilySizeFormatter.DnToRadiusFt(50)));
        Assert.Equal(65, FamilySizeFormatter.ToDn(FamilySizeFormatter.DnToRadiusFt(65)));
        Assert.Equal(100, FamilySizeFormatter.ToDn(FamilySizeFormatter.DnToRadiusFt(100)));
    }

    [Fact]
    public void DnToRadiusFt_IsPositive_ForPositiveDn()
    {
        var r = FamilySizeFormatter.DnToRadiusFt(50);
        Assert.True(r > 0);
    }
}
