using SmartCon.Core.Math.FormulaEngine.Solver;
using Xunit;


namespace SmartCon.Tests.Core.Services;

public sealed class ParameterResolutionFlowTests
{
    [Fact]
    public void SolveFor_DiameterHalf_ReturnsDoubled()
    {
        var result = FormulaSolver.SolveForStatic("diameter / 2", "diameter", 0.0821);
        Assert.NotNull(result);
        Assert.Equal(0.1642, result!.Value, 1e-4);
    }

    [Fact]
    public void SolveFor_RadiusPlusOffset_ReturnsAdjusted()
    {
        var result = FormulaSolver.SolveForStatic("r + 0.001", "r", 0.0821);
        Assert.NotNull(result);
        Assert.Equal(0.0811, result!.Value, 1e-4);
    }

    [Fact]
    public void SolveFor_SizeIsLinear_ReturnsValue()
    {
        var result = FormulaSolver.SolveForStatic("x * 2", "x", 30.0);
        Assert.NotNull(result);
        Assert.Equal(15.0, result!.Value, 1e-6);
    }

    [Fact]
    public void SolveFor_ComplexFormula_ReturnsNull_ExpectAdapter()
    {
        var result = FormulaSolver.SolveForStatic("x * x + 1", "x", 50.0);
        Assert.NotNull(result);
        Assert.Equal(7.0, result!.Value, 0.1);
    }

    [Fact]
    public void SolveFor_SizeLookupFormula_ReturnsNull_ExpectAdapter()
    {
        var result = FormulaSolver.SolveForStatic(
            "size_lookup(\"Table1\", radius, \"default\", diameter)", "diameter", 25.0);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractVariables_DiameterFormula_FindsDiameter()
    {
        var vars = FormulaSolver.ExtractVariablesStatic("diameter / 2");
        Assert.Contains("diameter", vars, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractVariables_SizeLookupFormula_FindsQueryParam()
    {
        var vars = FormulaSolver.ExtractVariablesStatic(
            "size_lookup(\"Table1\", radius, \"default\", DN)");
        Assert.Contains("DN", vars, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseSizeLookup_Valid_ReturnsTableAndTarget()
    {
        var r = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(\"DN_Table\", radius, \"default\", nomDiam)");
        Assert.NotNull(r);
        Assert.Equal("DN_Table", r!.Value.TableName);
        Assert.Equal("radius", r.Value.TargetParameter);
        Assert.Equal("nomDiam", r.Value.QueryParameters[0]);
    }

    [Theory]
    [InlineData("diameter / 2", 0.0821, "diameter", 0.1642)]
    [InlineData("radius * 2 + 0.001", 0.0656, "radius", 0.03275)]
    [InlineData("x * 3 - 1", 8.0, "x", 3.0)]
    public void SolveFor_VariousLinearFormulas_ReturnsCorrect(
        string formula, double target, string variable, double expected)
    {
        var result = FormulaSolver.SolveForStatic(formula, variable, target);
        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value, 1e-3);
    }

    [Fact]
    public void SolveFor_NonLinearQuadratic_SolvedByBisection()
    {
        var result = FormulaSolver.SolveForStatic("x * x", "x", 25.0);
        Assert.NotNull(result);
        Assert.Equal(5.0, result!.Value, 1e-3);
    }
}
