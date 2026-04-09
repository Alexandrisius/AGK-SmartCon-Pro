using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

public sealed class SizeTableRowTests
{
    [Fact]
    public void RequiredProperties_SetCorrectly()
    {
        var row = new SizeTableRow
        {
            TargetColumnIndex = 1,
            TargetRadiusFt = 0.082021,
            ConnectorRadiiFt = new Dictionary<int, double>
            {
                [0] = 0.082021,
                [1] = 0.082021,
                [2] = 0.065617
            }
        };

        Assert.Equal(1, row.TargetColumnIndex);
        Assert.Equal(0.082021, row.TargetRadiusFt, 8);
        Assert.Equal(3, row.ConnectorRadiiFt.Count);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var row = new SizeTableRow
        {
            TargetColumnIndex = 0,
            TargetRadiusFt = 0.0,
            ConnectorRadiiFt = new Dictionary<int, double>()
        };

        Assert.Empty(row.NonSizeParameterValues);
    }

    [Fact]
    public void Record_Equality_SameValues()
    {
        var radii = new Dictionary<int, double> { [0] = 0.1 };
        var a = new SizeTableRow { TargetColumnIndex = 1, TargetRadiusFt = 0.1, ConnectorRadiiFt = radii };
        var b = a with { };

        Assert.Equal(a.TargetColumnIndex, b.TargetColumnIndex);
        Assert.Equal(a.TargetRadiusFt, b.TargetRadiusFt);
        Assert.Equal(a.ConnectorRadiiFt, b.ConnectorRadiiFt);
    }

    [Fact]
    public void Record_Equality_DifferentRadii()
    {
        var a = new SizeTableRow
        {
            TargetColumnIndex = 1,
            TargetRadiusFt = 0.1,
            ConnectorRadiiFt = new Dictionary<int, double> { [0] = 0.1 }
        };
        var b = new SizeTableRow
        {
            TargetColumnIndex = 1,
            TargetRadiusFt = 0.2,
            ConnectorRadiiFt = new Dictionary<int, double> { [0] = 0.2 }
        };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NonSizeParameterValues_CanStoreData()
    {
        var row = new SizeTableRow
        {
            TargetColumnIndex = 1,
            TargetRadiusFt = 0.1,
            ConnectorRadiiFt = new Dictionary<int, double> { [0] = 0.1 },
            NonSizeParameterValues = new Dictionary<string, string>
            {
                ["ТИП_ОРГАНА"] = "2"
            }
        };

        Assert.Equal("2", row.NonSizeParameterValues["ТИП_ОРГАНА"]);
    }
}
