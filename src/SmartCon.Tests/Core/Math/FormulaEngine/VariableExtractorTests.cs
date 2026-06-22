using SmartCon.Core.Math.FormulaEngine;
using SmartCon.Core.Math.FormulaEngine.Solver;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public sealed class VariableExtractorTests
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

    [Fact]
    public void Extract_VariableWithSpace_MergedIntoOneToken()
    {
        // Регрессия: импортируемое семейство вентилятора содержало формулу
        // "ADSK_Номинальная мощность / ADSK_Коэффициент мощности", и без
        // greedy identifier tokenizer ExtractVariables возвращал [] — из-за
        // чего ни одна formula не считалась ссылающейся на catalog inputs,
        // и DisableFormulas/RestoreFormulas не делали ничего.
        var vars = Extract("ADSK_Номинальная мощность / ADSK_Коэффициент мощности");
        Assert.Contains("ADSK_Номинальная мощность", vars);
        Assert.Contains("ADSK_Коэффициент мощности", vars);
        Assert.Equal(2, vars.Count);
    }

    [Fact]
    public void Extract_VariableWithSpace_StopsAtOperator()
    {
        // Сразу после identifier — оператор, а не whitespace.
        var vars = Extract("Присоединительный диаметр / 2");
        Assert.Single(vars);
        Assert.Contains("Присоединительный диаметр", vars);
    }

    [Fact]
    public void Extract_VariableWithSpace_StopsAtParen()
    {
        // sqrt(...) — аргумент тоже может быть identifier с пробелом.
        var vars = Extract("sqrt(ADSK_Количество фаз числовое)");
        Assert.Single(vars);
        Assert.Contains("ADSK_Количество фаз числовое", vars);
    }

    [Fact]
    public void Extract_MixedCyrillicAndLatinVariableWithSpace()
    {
        var vars = Extract("ADSK_Частота вращения вентилятора / 2");
        Assert.Single(vars);
        Assert.Contains("ADSK_Частота вращения вентилятора", vars);
    }
}
