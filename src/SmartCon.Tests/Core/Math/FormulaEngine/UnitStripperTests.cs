using SmartCon.Core.Math.FormulaEngine;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public sealed class UnitStripperTests
{
    [Theory]
    [InlineData("DN < 50 mm", "DN < 50")]
    [InlineData("DN / 2 - 1 mm", "DN / 2 - 1")]
    [InlineData("Length / 1000 mm", "Length / 1000")]
    [InlineData("50 мм", "50")]
    [InlineData("100 см", "100")]
    [InlineData("2 м", "2")]
    [InlineData("3.5 ft", "3.5")]
    [InlineData("12 in", "12")]
    public void Strip_RemovesUnitSuffixes(string input, string expected)
    {
        Assert.Equal(expected, UnitStripper.Strip(input));
    }

    [Fact]
    public void Strip_IfFormula_RemovesAllUnits()
    {
        var input = "if(DN < 50 mm, DN / 2 - 1 mm, DN / 2 - 2 mm)";
        var result = UnitStripper.Strip(input);
        Assert.DoesNotContain("mm", result);
        Assert.Contains("DN", result);
    }

    [Theory]
    [InlineData("x + 5", "x + 5")]
    [InlineData("sin(pi() / 2)", "sin(pi() / 2)")]
    [InlineData("42", "42")]
    public void Strip_NoUnits_Unchanged(string input, string expected)
    {
        Assert.Equal(expected, UnitStripper.Strip(input));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("   ", "   ")]
    public void Strip_EmptyOrNull_ReturnsEmpty(string? input, string expected)
    {
        Assert.Equal(expected, UnitStripper.Strip(input!));
    }

    [Fact]
    public void Strip_UnitConversionPattern_Removed()
    {
        // "(x / 1)" → "/ 1)" matched → replaced with ")" → "(x )"
        var result = UnitStripper.Strip("(x / 1)");
        Assert.Equal("(x )", result);
    }

    [Fact]
    public void Strip_SquareMeters()
    {
        var result = UnitStripper.Strip("round(Area / 1 м²) * (1 м²)");
        Assert.DoesNotContain("м²", result);
    }
}
