using SmartCon.Core.Math;
using Xunit;

namespace SmartCon.Tests.Core.Services;

/// <summary>
/// Тесты бизнес-логики потока параметрического подбора (S4) — только чистая математика.
/// ParameterDependency, PipeConnectionSession и интерфейсы IParameterResolver/ILookupTableService
/// транзитивно ссылаются на RevitAPI (ElementId, BuiltInParameter) и не могут быть
/// проверены без RevitAPI.dll в output (Private=false в Tests). Их поведение тестируется
/// в интеграционных тестах с Revit runtime. Здесь — только pure C# алгоритмы S4.
/// </summary>
public sealed class ParameterResolutionFlowTests
{
    private const double Eps = 1e-6;

    // ── Логика пропуска S4 (BuildResolutionPlan) ──────────────────────────

    [Fact]
    public void ResolutionFlow_SameRadius_ShouldSkip()
    {
        double staticRadius  = 0.0821;
        double dynamicRadius = 0.0821;

        bool shouldSkip = System.Math.Abs(staticRadius - dynamicRadius) < Eps;

        Assert.True(shouldSkip);
    }

    [Fact]
    public void ResolutionFlow_DifferentRadius_ShouldNotSkip()
    {
        double staticRadius  = 0.0821;
        double dynamicRadius = 0.0656;

        bool shouldSkip = System.Math.Abs(staticRadius - dynamicRadius) < Eps;

        Assert.False(shouldSkip);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.0821, 0.0821)]
    [InlineData(0.0821 + 5e-7, 0.0821)]
    public void ResolutionFlow_RadiusWithinTolerance_Skipped(double a, double b)
    {
        bool shouldSkip = System.Math.Abs(a - b) < Eps;
        Assert.True(shouldSkip);
    }

    [Theory]
    [InlineData(0.0821, 0.0492)]
    [InlineData(0.0821, 0.0656)]
    [InlineData(0.1312, 0.0821)]
    public void ResolutionFlow_RadiusOutsideTolerance_NotSkipped(double a, double b)
    {
        bool shouldSkip = System.Math.Abs(a - b) < Eps;
        Assert.False(shouldSkip);
    }

    // ── Логика выбора ближайшего радиуса (таблица) ───────────────────────

    [Fact]
    public void NearestRadius_FromList_ReturnsClosest()
    {
        double target  = 0.0821;
        double[] table = [0.0328, 0.0492, 0.0656, 0.0820, 0.1148];

        double nearest = table.MinBy(r => System.Math.Abs(r - target));

        Assert.Equal(0.0820, nearest, 1e-9);
    }

    [Fact]
    public void NearestRadius_ExactMatch_ReturnsSame()
    {
        double target  = 0.0656;
        double[] table = [0.0328, 0.0492, 0.0656, 0.0820];

        double nearest = table.MinBy(r => System.Math.Abs(r - target));

        Assert.Equal(0.0656, nearest, 1e-9);
    }

    [Fact]
    public void NearestRadius_DeltaExceedsEps_ExpectAdapter()
    {
        double target   = 0.0821;
        double nearest  = 0.0820;
        double delta    = System.Math.Abs(target - nearest);

        Assert.True(delta > Eps);
    }

    // ── Unit-конверсии внутренних единиц ─────────────────────────────────

    [Theory]
    [InlineData(0.0821, 50.0)]   // ~DN50: radius 25mm = 0.0821ft → diameter 50mm
    [InlineData(0.0492, 30.0)]   // ~DN32: radius 15mm ≈ 0.0492ft → diameter 30mm
    [InlineData(0.1312, 80.0)]   // ~DN80: radius 40mm ≈ 0.1312ft → diameter 80mm
    public void FeetToMm_DiameterConversion(double radiusFt, double expectedDiamMm)
    {
        double diamMm = System.Math.Round(radiusFt * 2.0 * 304.8);
        Assert.Equal(expectedDiamMm, diamMm, 1.0);
    }

    // ── MiniFormulaSolver в контексте S4 ─────────────────────────────────

    [Fact]
    public void SolveFor_DiameterHalf_ReturnsDoubled()
    {
        var result = MiniFormulaSolver.SolveFor("diameter / 2", "diameter", 0.0821);
        Assert.NotNull(result);
        Assert.Equal(0.1642, result!.Value, 1e-4);
    }

    [Fact]
    public void SolveFor_RadiusPlusOffset_ReturnsAdjusted()
    {
        // radius = r + 0.001 → r = target - 0.001
        var result = MiniFormulaSolver.SolveFor("r + 0.001", "r", 0.0821);
        Assert.NotNull(result);
        Assert.Equal(0.0811, result!.Value, 1e-4);
    }

    [Fact]
    public void SolveFor_SizeIsLinear_ReturnsValue()
    {
        // size = x * 2 → x = 15 when target = 30
        var result = MiniFormulaSolver.SolveFor("x * 2", "x", 30.0);
        Assert.NotNull(result);
        Assert.Equal(15.0, result!.Value, Eps);
    }

    [Fact]
    public void SolveFor_ComplexFormula_ReturnsNull_ExpectAdapter()
    {
        var result = MiniFormulaSolver.SolveFor("x * x + 1", "x", 50.0);
        Assert.Null(result);
    }

    [Fact]
    public void SolveFor_SizeLookupFormula_ReturnsNull_ExpectAdapter()
    {
        var result = MiniFormulaSolver.SolveFor(
            "size_lookup(Table1, radius, \"default\", diameter)", "diameter", 25.0);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractVariables_DiameterFormula_FindsDiameter()
    {
        var vars = MiniFormulaSolver.ExtractVariables("diameter / 2");
        Assert.Contains("diameter", vars, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractVariables_SizeLookupFormula_FindsQueryParam()
    {
        var vars = MiniFormulaSolver.ExtractVariables(
            "size_lookup(Table1, radius, \"default\", DN)");
        Assert.Contains("DN", vars, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSizeLookup_Valid_ReturnsTableAndTarget()
    {
        var r = MiniFormulaSolver.ParseSizeLookup(
            "size_lookup(DN_Table, radius, \"default\", nomDiam)");
        Assert.NotNull(r);
        Assert.Equal("DN_Table", r!.Value.TableName);
        Assert.Equal("radius",   r.Value.TargetParameter);
        Assert.Equal("nomDiam",  r.Value.QueryParameters[0]);
    }
}
