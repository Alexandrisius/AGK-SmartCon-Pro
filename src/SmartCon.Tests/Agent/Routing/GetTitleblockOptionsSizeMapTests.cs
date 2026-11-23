using Xunit;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Tests.Agent.Routing;

/// <summary>
/// Pure selection half of the get_titleblock_options sizeMap (C6-08/S1-R6):
/// only a parameter whose distinct values have produced at least TWO distinct
/// sheet widths in the album is size-driving; anything else (a parameter that
/// varies while the size stays constant, or a constant parameter) is honestly
/// left out, and the entry carries no per-value note (S1-R6: one note per
/// sizeMap block). The document half is pinned by the integration tests.
/// </summary>
public sealed class GetTitleblockOptionsSizeMapTests
{
    private static Dictionary<string, (double Width, double Height)> Sizes(
        params (string Key, double Width, double Height)[] rows)
    {
        var sizes = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        foreach (var row in rows)
            sizes[row.Key] = (row.Width, row.Height);
        return sizes;
    }

    [Fact]
    public void ParameterWithTwoWidths_IsSelectedWithSortedValues()
    {
        var observed = new Dictionary<string, Dictionary<string, (int, string)>>(
            StringComparer.Ordinal)
        {
            ["Multiplicity"] = new(StringComparer.Ordinal)
            {
                ["2"] = (3, "594x420"),
                ["1"] = (2, "420x297")
            }
        };

        var result = GetTitleblockOptionsTool.SelectSizeDriving(
            observed, Sizes(("420x297", 420.0, 297.0), ("594x420", 594.0, 420.0)));

        var entry = Assert.Single(result);
        Assert.Equal("Multiplicity", entry.Parameter);
        Assert.Equal(2, entry.Values.Count);
        Assert.Equal("1", entry.Values[0].Value);
        Assert.Equal(2, entry.Values[0].SheetCount);
        Assert.Equal(420.0, entry.Values[0].WidthMm);
        Assert.Equal(297.0, entry.Values[0].HeightMm);
        Assert.Equal("2", entry.Values[1].Value);
        Assert.Equal(594.0, entry.Values[1].WidthMm);
    }

    [Fact]
    public void ParameterWithOneWidthOnly_IsNotSizeDriving()
    {
        // A parameter that varies while every sheet keeps the same size tells
        // the caller nothing about the paper format.
        var observed = new Dictionary<string, Dictionary<string, (int, string)>>(
            StringComparer.Ordinal)
        {
            ["DrawnBy"] = new(StringComparer.Ordinal)
            {
                ["AA"] = (1, "420x297"),
                ["BB"] = (2, "420x297")
            }
        };

        var result = GetTitleblockOptionsTool.SelectSizeDriving(
            observed, Sizes(("420x297", 420.0, 297.0)));

        Assert.Empty(result);
    }

    [Fact]
    public void ConstantParameter_IsNotSizeDriving()
    {
        var observed = new Dictionary<string, Dictionary<string, (int, string)>>(
            StringComparer.Ordinal)
        {
            ["ProjectCode"] = new(StringComparer.Ordinal)
            {
                ["X1"] = (5, "420x297")
            }
        };

        var result = GetTitleblockOptionsTool.SelectSizeDriving(
            observed, Sizes(("420x297", 420.0, 297.0)));

        Assert.Empty(result);
    }
}
