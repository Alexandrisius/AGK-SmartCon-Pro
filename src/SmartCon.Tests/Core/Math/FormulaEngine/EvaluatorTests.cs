using SmartCon.Core.Math.FormulaEngine;
using SmartCon.Core.Math.FormulaEngine.Ast;
using SmartCon.Core.Math.FormulaEngine.Solver;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public sealed class EvaluatorTests
{
    private static double Eval(string formula, Dictionary<string, double>? vars = null)
    {
        var stripped = UnitStripper.Strip(formula);
        var tokens = Tokenizer.Tokenize(stripped);
        var ast = Parser.Parse(tokens);
        return Evaluator.Evaluate(ast, vars ?? new Dictionary<string, double>());
    }

    // ── Арифметика ──────────────────────────────────────────────────────

    [Fact] public void Arithmetic_Number() => Assert.Equal(42.0, Eval("42"));
    [Fact] public void Arithmetic_Add() => Assert.Equal(5.0, Eval("2 + 3"));
    [Fact] public void Arithmetic_Sub() => Assert.Equal(6.0, Eval("10 - 4"));
    [Fact] public void Arithmetic_Mul() => Assert.Equal(21.0, Eval("3 * 7"));
    [Fact] public void Arithmetic_Div() => Assert.Equal(2.5, Eval("10 / 4"));
    [Fact] public void Arithmetic_Parens() => Assert.Equal(20.0, Eval("(2 + 3) * 4"));
    [Fact] public void Arithmetic_UnaryMinus() => Assert.Equal(-5.0, Eval("-5"));
    [Fact] public void Arithmetic_Power() => Assert.Equal(1024.0, Eval("2 ^ 10"));
    [Fact] public void Arithmetic_Modulo() => Assert.Equal(1.0, Eval("10 % 3"));
    [Fact] public void Arithmetic_Precedence() => Assert.Equal(13.0, Eval("2 + 3 * 4 - 1"));

    [Fact]
    public void Arithmetic_NestedParens()
    {
        Assert.Equal(4.0, Eval("((2 + 3) * (10 - 6)) / 5"));
    }

    [Fact]
    public void Arithmetic_DivisionByZero_ThrowsFormulaParseException()
    {
        Assert.Throws<FormulaParseException>(() => Eval("1 / 0"));
    }

    // ── Переменные ──────────────────────────────────────────────────────

    [Fact]
    public void Variable_Simple()
    {
        Assert.Equal(20.0, Eval("x * 2", new() { ["x"] = 10 }));
    }

    [Fact]
    public void Variable_Multiple()
    {
        Assert.Equal(5.0, Eval("(a + b) / 2", new() { ["a"] = 6, ["b"] = 4 }));
    }

    [Fact]
    public void Variable_Unknown_ReturnsZero()
    {
        Assert.Equal(5.0, Eval("x + 5"));
    }

    [Fact]
    public void Variable_CaseInsensitive()
    {
        Assert.Equal(25.0, Eval("Diameter / 2", new() { ["diameter"] = 50 }));
    }

    [Fact]
    public void Variable_Cyrillic()
    {
        Assert.Equal(25.0, Eval("Диаметр / 2", new() { ["Диаметр"] = 50 }));
    }

    // ── Сравнения ───────────────────────────────────────────────────────

    [Fact] public void Compare_LessThan_True() => Assert.Equal(1.0, Eval("5 < 10"));
    [Fact] public void Compare_LessThan_False() => Assert.Equal(0.0, Eval("10 < 5"));
    [Fact] public void Compare_Equal_True() => Assert.Equal(1.0, Eval("5 = 5"));
    [Fact] public void Compare_NotEqual_False() => Assert.Equal(0.0, Eval("5 <> 5"));
    [Fact] public void Compare_LessEqual_True() => Assert.Equal(1.0, Eval("5 <= 5"));
    [Fact] public void Compare_GreaterEqual_False() => Assert.Equal(0.0, Eval("5 >= 10"));

    // ── IF — простые ────────────────────────────────────────────────────

    [Fact]
    public void If_TrueCondition()
    {
        Assert.Equal(10.0, Eval("if(1, 10, 20)"));
    }

    [Fact]
    public void If_FalseCondition()
    {
        Assert.Equal(20.0, Eval("if(0, 10, 20)"));
    }

    [Fact]
    public void If_WithComparison_TrueBranch()
    {
        Assert.Equal(39.0, Eval("if(x < 100, x / 2 - 1, x / 2 - 2)", new() { ["x"] = 80 }));
    }

    [Fact]
    public void If_WithComparison_FalseBranch()
    {
        Assert.Equal(58.0, Eval("if(x < 100, x / 2 - 1, x / 2 - 2)", new() { ["x"] = 120 }));
    }

    [Fact]
    public void If_EqualityCondition()
    {
        Assert.Equal(100.0, Eval("if(DN = 0, 100, DN * 2)", new() { ["DN"] = 0 }));
    }

    // ── IF — вложенные ──────────────────────────────────────────────────

    [Theory]
    [InlineData(5, 1)]
    [InlineData(15, 2)]
    [InlineData(25, 3)]
    public void If_Nested_ThreeLevels(double x, double expected)
    {
        Assert.Equal(expected, Eval("if(x < 10, 1, if(x < 20, 2, 3))", new() { ["x"] = x }));
    }

    [Fact]
    public void If_TripleNested()
    {
        Assert.Equal(30.0, Eval(
            "if(a < 1, 10, if(a < 2, 20, if(a < 3, 30, 40)))",
            new() { ["a"] = 2.5 }));
    }

    [Fact]
    public void If_DeepNesting_20Levels()
    {
        // Генерируем: if(x<1, 0, if(x<2, 1, if(x<3, 2, ... if(x<20, 19, 20)...)))
        var formula = "20";
        for (int i = 19; i >= 0; i--)
            formula = $"if(x < {i + 1}, {i}, {formula})";

        Assert.Equal(10.0, Eval(formula, new() { ["x"] = 10.5 }));
        Assert.Equal(0.0, Eval(formula, new() { ["x"] = 0.5 }));
        Assert.Equal(19.0, Eval(formula, new() { ["x"] = 19.5 }));
    }

    // ── AND/OR/NOT ──────────────────────────────────────────────────────

    [Fact]
    public void And_BothTrue()
    {
        Assert.Equal(1.0, Eval("if(AND(a > 5, b < 10), 1, 0)", new() { ["a"] = 6, ["b"] = 8 }));
    }

    [Fact]
    public void And_OneFalse()
    {
        Assert.Equal(0.0, Eval("if(AND(a > 5, b < 10), 1, 0)", new() { ["a"] = 4, ["b"] = 8 }));
    }

    [Fact]
    public void Or_OneTrue()
    {
        Assert.Equal(1.0, Eval("if(OR(a = 5, b = 10), 1, 0)", new() { ["a"] = 5, ["b"] = 3 }));
    }

    [Fact]
    public void Not_True()
    {
        Assert.Equal(1.0, Eval("if(NOT(a > 5), 1, 0)", new() { ["a"] = 3 }));
    }

    [Fact]
    public void Complex_AndOrNested()
    {
        Assert.Equal(10.0, Eval(
            "if(AND(OR(a > 1, b > 1), c < 5), 10, 20)",
            new() { ["a"] = 0, ["b"] = 2, ["c"] = 3 }));
    }

    // ── Тригонометрия ───────────────────────────────────────────────────

    [Fact] public void Trig_Sin0() => Assert.Equal(0.0, Eval("sin(0)"), 10);
    [Fact] public void Trig_Cos0() => Assert.Equal(1.0, Eval("cos(0)"), 10);

    [Fact]
    public void Trig_SinPiHalf()
    {
        Assert.Equal(1.0, Eval("sin(pi() / 2)"), 10);
    }

    [Fact]
    public void Trig_AtanTimesFour_EqualsPi()
    {
        Assert.Equal(System.Math.PI, Eval("atan(1) * 4"), 10);
    }

    [Fact]
    public void Trig_Asin1()
    {
        Assert.Equal(System.Math.PI / 2, Eval("asin(1)"), 10);
    }

    // ── Математика ──────────────────────────────────────────────────────

    [Fact] public void Math_Abs() => Assert.Equal(3.7, Eval("abs(-3.7)"), 10);
    [Fact] public void Math_Sqrt() => Assert.Equal(5.0, Eval("sqrt(25)"), 10);

    [Fact]
    public void Math_Round_AwayFromZero()
    {
        // Revit: round(2.5) = 3 (MidpointRounding.AwayFromZero)
        Assert.Equal(3.0, Eval("round(2.5)"));
    }

    [Fact]
    public void Math_Roundup()
    {
        Assert.Equal(3.0, Eval("roundup(2.2)"));
    }

    [Fact]
    public void Math_Rounddown()
    {
        Assert.Equal(2.0, Eval("rounddown(2.8)"));
    }

    [Fact]
    public void Math_MinMax()
    {
        Assert.Equal(3.0, Eval("min(3, 7)"));
        Assert.Equal(7.0, Eval("max(3, 7)"));
    }

    // ── log / ln / exp ──────────────────────────────────────────────────

    [Fact] public void Log_1000() => Assert.Equal(3.0, Eval("log(1000)"), 10);

    [Fact]
    public void Ln_Exp_Roundtrip()
    {
        Assert.Equal(3.0, Eval("ln(exp(3))"), 10);
    }

    [Fact] public void Exp_Zero() => Assert.Equal(1.0, Eval("exp(0)"), 10);

    [Fact]
    public void Log_Plus_Ln()
    {
        Assert.Equal(2.0, Eval("log(100) + ln(1)"), 10);
    }

    // ── Константы ───────────────────────────────────────────────────────

    [Fact]
    public void Constant_Pi()
    {
        Assert.Equal(System.Math.PI, Eval("pi()"), 10);
    }

    [Fact]
    public void Constant_E()
    {
        Assert.Equal(System.Math.E, Eval("e"), 10);
    }

    [Fact]
    public void Constant_PiWithoutParens()
    {
        Assert.Equal(System.Math.PI, Eval("pi"), 10);
    }

    // ── size_lookup ─────────────────────────────────────────────────────

    [Fact]
    public void SizeLookup_ReturnsZero()
    {
        Assert.Equal(0.0, Eval("size_lookup(\"T\", p, \"d\", DN)"));
    }

    [Fact]
    public void SizeLookup_InsideIf_FalseBranch()
    {
        // x > 0 true → size_lookup → 0.0
        Assert.Equal(0.0, Eval("if(x > 0, size_lookup(\"T\", p, \"d\", DN), 5)", new() { ["x"] = 1 }));
    }

    // ── Единицы измерения (через pipeline) ──────────────────────────────

    [Fact]
    public void Units_AreStripped()
    {
        Assert.Equal(1.0, Eval("if(DN < 50 mm, 1, 0)", new() { ["DN"] = 40 }));
    }
}
