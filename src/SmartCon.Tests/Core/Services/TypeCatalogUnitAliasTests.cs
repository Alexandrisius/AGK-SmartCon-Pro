using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class TypeCatalogUnitAliasTests
{
    // ── Length canonical mappings ───────────────────────────────────────

    [Theory]
    [InlineData("millimeters")]
    [InlineData("millimeter")]
    [InlineData("milimeters")] // sic — Autodesk docs typo
    [InlineData("mm")]
    [InlineData("MILLIMETERS")]
    [InlineData("Millimeters")]
    [InlineData("  mm  ")]
    public void Normalize_Length_Millimeters(string input)
    {
        Assert.Equal("millimeters", TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("centimeters")]
    [InlineData("centimeter")]
    [InlineData("cm")]
    [InlineData("CM")]
    public void Normalize_Length_Centimeters(string input)
    {
        Assert.Equal("centimeters", TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("decimeters")]
    [InlineData("decimeter")]
    [InlineData("dm")]
    public void Normalize_Length_Decimeters(string input)
    {
        Assert.Equal("decimeters", TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("meters")]
    [InlineData("meter")]
    [InlineData("m")]
    public void Normalize_Length_Meters(string input)
    {
        Assert.Equal("meters", TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("inches")]
    [InlineData("inch")]
    [InlineData("in")]
    public void Normalize_Length_Inches(string input)
    {
        Assert.Equal("inches", TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("feet")]
    [InlineData("foot")]
    [InlineData("ft")]
    public void Normalize_Length_Feet(string input)
    {
        Assert.Equal("feet", TypeCatalogUnitAlias.Normalize(input));
    }

    // ── Angle canonical mappings ────────────────────────────────────────

    [Theory]
    [InlineData("degrees")]
    [InlineData("degree")]
    [InlineData("decimal_degrees")]
    [InlineData("deg")]
    public void Normalize_Angle_Degrees(string input)
    {
        Assert.Equal("degrees", TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("radians")]
    [InlineData("radian")]
    [InlineData("rad")]
    public void Normalize_Angle_Radians(string input)
    {
        Assert.Equal("radians", TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("grads")]
    [InlineData("grad")]
    public void Normalize_Angle_Grads(string input)
    {
        Assert.Equal("grads", TypeCatalogUnitAlias.Normalize(input));
    }

    // ── Area / Volume / Power / Electrical (one test per category) ────

    [Theory]
    [InlineData("square_millimeters")]
    [InlineData("square_millimeter")]
    [InlineData("sq_mm")]
    public void Normalize_Area_SquareMillimeters(string input)
    {
        Assert.Equal("square_millimeters", TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("cubic_meters")]
    [InlineData("cubic_meter")]
    [InlineData("cu_m")]
    public void Normalize_Volume_CubicMeters(string input)
    {
        Assert.Equal("cubic_meters", TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("watts")]
    [InlineData("watt")]
    [InlineData("w")]
    public void Normalize_Power_Watts(string input)
    {
        Assert.Equal("watts", TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("amperes")]
    [InlineData("ampere")]
    [InlineData("amps")]
    [InlineData("a")]
    public void Normalize_Electrical_Amperes(string input)
    {
        Assert.Equal("amperes", TypeCatalogUnitAlias.Normalize(input));
    }

    // ── Edge cases ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_EmptyOrNull_ReturnsNull(string? input)
    {
        Assert.Null(TypeCatalogUnitAlias.Normalize(input));
    }

    [Theory]
    [InlineData("unknown_unit")]
    [InlineData("xyz")]
    [InlineData("kilogram")] // Not supported in Type Catalog spec
    [InlineData("kelvin")]
    public void Normalize_Unknown_ReturnsNull(string input)
    {
        Assert.Null(TypeCatalogUnitAlias.Normalize(input));
    }

    [Fact]
    public void SupportedAliases_ContainsExpectedCanonicalKeys()
    {
        var aliases = TypeCatalogUnitAlias.SupportedAliases;
        Assert.Contains("millimeters", aliases);
        Assert.Contains("meters", aliases);
        Assert.Contains("feet", aliases);
        Assert.Contains("degrees", aliases);
        Assert.Contains("radians", aliases);
        Assert.Contains("square_meters", aliases);
        Assert.Contains("cubic_meters", aliases);
        Assert.Contains("watts", aliases);
        Assert.Contains("amperes", aliases);
        Assert.Contains("volts", aliases);
        // grads входит в SupportedAliases потому что R19-R20 имеет DisplayUnitType.DUT_GRADS.
        // На R2025+ ResolveSourceUnitTypeId возвращает null для "grads" (нет UnitTypeId.Grads).
        Assert.Contains("grads", aliases);
    }
}