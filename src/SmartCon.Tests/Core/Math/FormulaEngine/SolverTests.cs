using SmartCon.Core.Math.FormulaEngine;
using SmartCon.Core.Math.FormulaEngine.Solver;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public class SolverTests
{
    private static readonly FormulaSolver Solver = new();

    private static double Solve(string formula, string varName, double target,
        Dictionary<string, double>? others = null)
        => Solver.SolveFor(formula, varName, target,
            others ?? new Dictionary<string, double>());

    private static double? SolveStatic(string formula, string varName, double target,
        Dictionary<string, double>? others = null)
        => FormulaSolver.SolveForStatic(formula, varName, target, others);

    // ── Линейные (совместимость с MiniFormulaSolver) ─────────────────────

    [Fact]
    public void Linear_DivideBy2()
    {
        Assert.Equal(50.0, Solve("x / 2", "x", 25), 6);
    }

    [Fact]
    public void Linear_MultiplyPlusConst()
    {
        Assert.Equal(5.0, Solve("x * 2 + 1", "x", 11), 6);
    }

    [Fact]
    public void Linear_Identity()
    {
        Assert.Equal(42.0, Solve("x + 0", "x", 42), 6);
    }

    [Fact]
    public void Linear_NamedVariable()
    {
        Assert.Equal(50.0, Solve("diameter / 2", "diameter", 25), 6);
    }

    [Fact]
    public void Linear_WithOtherVars()
    {
        Assert.Equal(5.0, Solve("x + y", "x", 15, new() { ["y"] = 10 }), 6);
    }

    [Fact]
    public void Linear_CyrillicVariable()
    {
        Assert.Equal(50.0, Solve("Диаметр / 2", "Диаметр", 25), 6);
    }

    [Fact]
    public void VariableNotInFormula_Throws()
    {
        Assert.Throws<FormulaParseException>(() => Solve("42", "x", 10));
    }

    [Fact]
    public void VariableNotInFormula_StaticReturnsNull()
    {
        Assert.Null(SolveStatic("42", "x", 10));
    }

    // ── Нелинейные (бисекция) ───────────────────────────────────────────

    [Fact]
    public void Bisection_XSquared()
    {
        var result = Solve("x * x", "x", 25);
        Assert.Equal(5.0, result, 4);
    }

    [Fact]
    public void Bisection_Sqrt()
    {
        Assert.Equal(25.0, Solve("sqrt(x)", "x", 5), 4);
    }

    [Fact]
    public void Bisection_Sin()
    {
        // sin(x) = 0.5 → x ≈ π/6 ≈ 0.5236
        var result = Solve("sin(x)", "x", 0.5);
        Assert.Equal(System.Math.PI / 6, result, 3);
    }

    [Fact]
    public void Bisection_XSquaredPlusX()
    {
        // x^2 + x = 6 → x = 2
        Assert.Equal(2.0, Solve("x ^ 2 + x", "x", 6), 4);
    }

    [Fact]
    public void Bisection_Log()
    {
        // log(x) = 2 → x = 100
        Assert.Equal(100.0, Solve("log(x)", "x", 2), 3);
    }

    [Fact]
    public void Bisection_Exp()
    {
        // exp(x) = 1 → x = 0
        Assert.Equal(0.0, Solve("exp(x)", "x", 1), 4);
    }

    // ── IF-формулы ──────────────────────────────────────────────────────

    [Fact]
    public void If_Linear_TrueBranch()
    {
        // if(x < 100, x / 2, x / 3), target=25 → x=50 (50<100 ✓, 50/2=25)
        Assert.Equal(50.0, Solve("if(x < 100, x / 2, x / 3)", "x", 25), 4);
    }

    [Fact]
    public void If_Linear_FalseBranch()
    {
        // if(x < 100, x / 2, x / 3), target=50 → x=100 (true branch: 100/2=50) or x=150 (false: 150/3=50)
        // Бисекция может найти любое из двух решений — проверяем что f(result) = target
        var result = Solve("if(x < 100, x / 2, x / 3)", "x", 50);
        var solver = new FormulaSolver();
        var check = solver.Evaluate("if(x < 100, x / 2, x / 3)", new Dictionary<string, double> { ["x"] = result });
        Assert.Equal(50.0, check, 4);
    }

    [Fact]
    public void If_WithKnownCondition()
    {
        // if(mode = 1, x * 2, x * 3), mode=1 → упрощается до x*2, target=10 → x=5
        Assert.Equal(5.0, Solve("if(mode = 1, x * 2, x * 3)", "x", 10,
            new() { ["mode"] = 1 }), 6);
    }

    [Fact]
    public void If_NestedWithLinearBranch()
    {
        // if(m = 1, x / 2, if(m = 2, x / 3, x / 4)), m=2 → x/3, target=10 → x=30
        Assert.Equal(30.0, Solve(
            "if(m = 1, x / 2, if(m = 2, x / 3, x / 4))", "x", 10,
            new() { ["m"] = 2 }), 6);
    }

    [Fact]
    public void If_WithAnd()
    {
        // if(AND(x > 10, x < 100), x * 2, x * 3), target=40
        // Multiple solutions: x=20 (AND true, 20*2=40) or x≈13.33 (AND false, 13.33*3=40)
        var result = Solve("if(AND(x > 10, x < 100), x * 2, x * 3)", "x", 40);
        var solver = new FormulaSolver();
        var check = solver.Evaluate("if(AND(x > 10, x < 100), x * 2, x * 3)",
            new Dictionary<string, double> { ["x"] = result });
        Assert.Equal(40.0, check, 4);
    }

    [Fact]
    public void SizeLookup_InsideIf_ReturnsNull()
    {
        Assert.Null(SolveStatic("if(x > 0, size_lookup(\"T\", p, \"d\", DN), 0)", "x", 5));
    }

    // ── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public void EmptyFormula_Throws()
    {
        Assert.Throws<FormulaParseException>(() => Solve("", "x", 10));
    }

    [Fact]
    public void SizeLookup_Formula_ReturnsNull()
    {
        Assert.Null(SolveStatic("size_lookup(\"T\", p, \"d\", DN)", "DN", 25));
    }

    [Fact]
    public void Formula_WithUnits_Solved()
    {
        // "DN / 2 mm" → after strip → "DN / 2"
        Assert.Equal(50.0, Solve("DN / 2 mm", "DN", 25), 6);
    }

    [Fact]
    public void DeepNestedIf_NoStackOverflow()
    {
        var formula = "20";
        for (int i = 19; i >= 0; i--)
            formula = $"if(m = {i}, x * {i + 1}, {formula})";

        // m=5, x * 6 = 30 → x = 5
        Assert.Equal(5.0, Solve(formula, "x", 30, new() { ["m"] = 5 }), 4);
    }
}
