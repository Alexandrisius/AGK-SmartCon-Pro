using SmartCon.Core.Math.FormulaEngine;
using SmartCon.Core.Math.FormulaEngine.Ast;
using SmartCon.Core.Math.FormulaEngine.Solver;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public sealed class FormulaEngineEdgeCaseTests
{
    private static double Eval(string formula, Dictionary<string, double>? vars = null)
    {
        var stripped = UnitStripper.Strip(formula);
        var tokens = Tokenizer.Tokenize(stripped);
        var ast = Parser.Parse(tokens);
        return Evaluator.Evaluate(ast, vars ?? new Dictionary<string, double>());
    }

    [Fact]
    public void DivisionByZero_ThrowsFormulaParseException()
    {
        Assert.Throws<FormulaParseException>(() => Eval("1 / 0"));
    }

    [Fact]
    public void DivisionByZero_Variable_ThrowsFormulaParseException()
    {
        Assert.Throws<FormulaParseException>(() => Eval("10 / x", new() { ["x"] = 0 }));
    }

    [Fact]
    public void ModuloByZero_Handled()
    {
        var result = Eval("10 % 0");
        Assert.False(double.IsNormal(result));
    }

    [Fact]
    public void NanInput_ProducesNan()
    {
        var result = Eval("x + 1", new() { ["x"] = double.NaN });
        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void NanInput_Multiplication_ProducesNan()
    {
        var result = Eval("x * 2", new() { ["x"] = double.NaN });
        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void InfinityInput_Addition_ProducesInfinity()
    {
        var result = Eval("x + 1", new() { ["x"] = double.PositiveInfinity });
        Assert.True(double.IsPositiveInfinity(result));
    }

    [Fact]
    public void InfinityMinusInfinity_ProducesNan()
    {
        var result = Eval("x - y", new()
        {
            ["x"] = double.PositiveInfinity,
            ["y"] = double.PositiveInfinity
        });
        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void LargeValue_Multiplication_NoOverflow()
    {
        var result = Eval("x * 2", new() { ["x"] = 1e150 });
        Assert.Equal(2e150, result);
    }

    [Fact]
    public void VeryLargeExponent_OverflowToInfinity()
    {
        var result = Eval("x ^ 1000", new() { ["x"] = 10.0 });
        Assert.True(double.IsPositiveInfinity(result));
    }

    [Fact]
    public void NestedIf_10Levels_EvaluatesCorrectly()
    {
        var formula = "0";
        for (int i = 9; i >= 0; i--)
            formula = $"if(x < {i + 1}, {i}, {formula})";

        Assert.Equal(5.0, Eval(formula, new() { ["x"] = 5.5 }));
        Assert.Equal(0.0, Eval(formula, new() { ["x"] = 0.5 }));
        Assert.Equal(9.0, Eval(formula, new() { ["x"] = 9.5 }));
    }

    [Fact]
    public void NestedIf_DeepWithVariableBranch()
    {
        var formula = "x * 0";
        for (int i = 14; i >= 0; i--)
            formula = $"if(m = {i}, x * {i + 1}, {formula})";

        Assert.Equal(10.0, Eval(formula, new() { ["x"] = 2, ["m"] = 4 }));
    }

    [Fact]
    public void EmptyFormula_ThrowsFormulaParseException()
    {
        Assert.Throws<FormulaParseException>(() => Eval(""));
    }

    [Fact]
    public void NegativeSqrt_ThrowsOrNan()
    {
        var result = Eval("sqrt(x)", new() { ["x"] = -4 });
        Assert.True(double.IsNaN(result));
    }

    [Fact]
    public void Solver_DivisionByZero_Throws()
    {
        Assert.Throws<FormulaParseException>(
            () => new FormulaSolver().SolveFor("x / 0", "x", 10, new Dictionary<string, double>()));
    }
}
