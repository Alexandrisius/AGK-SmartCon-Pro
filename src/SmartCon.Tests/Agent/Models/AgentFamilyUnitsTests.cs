using System.Globalization;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Models;

public sealed class AgentFamilyUnitsTests
{
    private const double Tolerance = 1e-9;

    [Theory]
    [InlineData("1200", "mm", 1200.0 / 304.8)]
    [InlineData("120", "cm", 120.0 / 30.48)]
    [InlineData("1.2", "m", 1.2 * 3.280839895013123)]
    [InlineData("12", "in", 1.0)]
    [InlineData("1", "ft", 1.0)]
    [InlineData("1200", "мм", 1200.0 / 304.8)]
    [InlineData("1,2", "м", 1.2 * 3.280839895013123)]
    public void Length_IsConvertedToFeet(string raw, string unit, double expected)
    {
        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Length, raw, unit, out var value, out var errorCode, out _),
            errorCode);

        Assert.Equal(expected, value.DoubleValue, Tolerance);
    }

    [Theory]
    [InlineData("90", "deg", 0.5)]
    [InlineData("180", "deg", 1.0)]
    public void Angle_IsConvertedToRadians(string raw, string unit, double expectedFactor)
    {
        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Angle, raw, unit, out var value, out _, out _));

        Assert.Equal(System.Math.PI * expectedFactor, value.DoubleValue, Tolerance);
    }

    [Fact]
    public void RadianAngle_IsKeptAsIs()
    {
        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Angle, "1", "rad", out var value, out _, out _));
        Assert.Equal(1.0, value.DoubleValue, Tolerance);
    }

    [Fact]
    public void AreaAndVolume_AreConverted()
    {
        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Area, "1", "m2", out var area, out _, out _));
        Assert.Equal(10.763910416709722, area.DoubleValue, Tolerance);

        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Volume, "1", "m3", out var volume, out _, out _));
        Assert.Equal(35.31466672148859, volume.DoubleValue, Tolerance);
    }

    [Fact]
    public void LengthWithoutUnit_ReturnsUnitRequired()
    {
        Assert.False(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Length, "1200", null, out _, out var errorCode, out var message));
        Assert.Equal("unit_required", errorCode);
        Assert.Contains("mm", message);
    }

    [Fact]
    public void LengthWithUnknownUnit_ReturnsUnitRequired()
    {
        Assert.False(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Length, "1200", "furlong", out _, out var errorCode, out _));
        Assert.Equal("unit_required", errorCode);
    }

    [Fact]
    public void Number_AcceptsNoUnitAndRejectsDimensionUnits()
    {
        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Number, "42.5", null, out var value, out _, out _));
        Assert.Equal(42.5, value.DoubleValue, Tolerance);

        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Number, "42", "number", out value, out _, out _));
        Assert.Equal(42.0, value.DoubleValue, Tolerance);

        Assert.False(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Number, "42", "mm", out _, out var errorCode, out _));
        Assert.Equal("invalid_value", errorCode);
    }

    [Fact]
    public void Text_KeepsRawValueAndRejectsUnit()
    {
        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Text, "  Заказчик  ", null, out var value, out _, out _));
        Assert.Equal("  Заказчик  ", value.TextValue);

        Assert.False(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Text, "текст", "mm", out _, out var errorCode, out _));
        Assert.Equal("invalid_value", errorCode);
    }

    [Theory]
    [InlineData("да", 1)]
    [InlineData("true", 1)]
    [InlineData("1", 1)]
    [InlineData("нет", 0)]
    [InlineData("false", 0)]
    [InlineData("0", 0)]
    public void YesNo_IsParsedInRussianAndEnglish(string raw, int expected)
    {
        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.YesNo, raw, null, out var value, out _, out _));
        Assert.Equal(expected, value.IntegerValue);
    }

    [Fact]
    public void YesNo_RejectsGarbage()
    {
        Assert.False(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.YesNo, "может быть", null, out _, out var errorCode, out _));
        Assert.Equal("invalid_value", errorCode);
    }

    [Fact]
    public void Integer_IsParsedAndFractionRejected()
    {
        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.WholeNumber, "-3", null, out var value, out _, out _));
        Assert.Equal(-3, value.IntegerValue);

        Assert.False(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.WholeNumber, "4.5", null, out _, out var errorCode, out _));
        Assert.Equal("invalid_value", errorCode);
    }

    [Fact]
    public void ElementId_SupportsNoneAndRejectsNegative()
    {
        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.ElementId, "none", null, out var value, out _, out _));
        Assert.Equal(-1, value.ElementIdValue);

        Assert.True(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.ElementId, "123", null, out value, out _, out _));
        Assert.Equal(123, value.ElementIdValue);

        Assert.False(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.ElementId, "-5", null, out _, out var errorCode, out _));
        Assert.Equal("invalid_value", errorCode);
    }

    [Fact]
    public void InvalidNumber_ReturnsInvalidValue()
    {
        Assert.False(AgentFamilyUnits.TryConvert(
            AgentFamilyValueKind.Length, "abc", "mm", out _, out var errorCode, out var message));
        Assert.Equal("invalid_value", errorCode);
        Assert.Contains("abc", message);
    }

    [Fact]
    public void InvariantFormatting_IsCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            Assert.Equal("1.5", AgentFamilyUnits.ToInvariant(1.5));
            Assert.Equal("1200", AgentFamilyUnits.ToInvariant(1200));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(" ММ ", "мм")]
    [InlineData("м²", "м2")]
    [InlineData("M3", "m3")]
    [InlineData("", null)]
    public void NormalizeUnit_HandlesLocalizedUnits(string unit, string? expected)
    {
        Assert.Equal(expected, AgentFamilyUnits.NormalizeUnit(unit));
    }
}