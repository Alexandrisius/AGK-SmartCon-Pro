using SmartCon.Core.Math.FormulaEngine;
using SmartCon.Core.Math.FormulaEngine.Solver;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public class VariableExtractorTests
{
    private static IReadOnlyList<string> Extract(string formula)
        => FormulaSolver.ExtractVariablesStatic(formula);

    [Fact]
    public void Extract_SimpleVariable()
    {
        var vars = Extract("x * 2");
        Assert.Contains("x", vars);
        Assert.Single(vars);
    }

    [Fact]
    public void Extract_MultipleVariables()
    {
        var vars = Extract("a + b * c");
        Assert.Equal(3, vars.Count);
        Assert.Contains("a", vars);
        Assert.Contains("b", vars);
        Assert.Contains("c", vars);
    }

    [Fact]
    public void Extract_NoVariables()
    {
        var vars = Extract("2 + 3 * 4");
        Assert.Empty(vars);
    }

    [Fact]
    public void Extract_FunctionNotVariable()
    {
        // sin, cos — функции, не переменные; x — переменная
        var vars = Extract("sin(x) + cos(x)");
        Assert.Single(vars);
        Assert.Contains("x", vars);
    }

    [Fact]
    public void Extract_IfVariables()
    {
        var vars = Extract("if(a < 5, b, c)");
        Assert.Contains("a", vars);
        Assert.Contains("b", vars);
        Assert.Contains("c", vars);
    }

    [Fact]
    public void Extract_SizeLookupQueryParams()
    {
        var vars = Extract("size_lookup(\"T\", p, \"d\", DN, PN)");
        Assert.Contains("DN", vars);
        Assert.Contains("PN", vars);
    }

    [Fact]
    public void Extract_PiAndE_NotVariables()
    {
        // pi() и e — константы, не переменные
        var vars = Extract("pi() * e");
        Assert.Empty(vars);
    }

    [Fact]
    public void Extract_CyrillicVariable()
    {
        var vars = Extract("Диаметр / 2");
        Assert.Single(vars);
        Assert.Contains("Диаметр", vars);
    }

    [Fact]
    public void Extract_BracketedVariable()
    {
        var vars = Extract("[Длина-A] + [Длина-B]");
        Assert.Equal(2, vars.Count);
    }

    [Fact]
    public void Extract_EmptyFormula()
    {
        var vars = Extract("");
        Assert.Empty(vars);
    }
}
