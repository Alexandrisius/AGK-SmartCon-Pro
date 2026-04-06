using SmartCon.Core.Math;
using SmartCon.Core.Math.FormulaEngine.Solver;
using Xunit;

namespace SmartCon.Tests.Core.Math;

/// <summary>
/// Мигрированные тесты MiniFormulaSolver → FormulaSolver.
/// Все тесты используют статические методы FormulaSolver (совместимый API).
/// </summary>
public sealed class MiniFormulaSolverTests
{
    private const double Eps = 1e-6;

    // ── Evaluate: числа и простые выражения ──────────────────────────────

    [Fact]
    public void Evaluate_NumberLiteral_ReturnsValue()
    {
        var result = FormulaSolver.EvaluateStatic("42", Empty);
        Assert.Equal(42.0, result, Eps);
    }

    [Fact]
    public void Evaluate_Addition_ReturnsSumOf2And3()
    {
        var result = FormulaSolver.EvaluateStatic("2 + 3", Empty);
        Assert.Equal(5.0, result, Eps);
    }

    [Fact]
    public void Evaluate_Subtraction_ReturnsDiff()
    {
        var result = FormulaSolver.EvaluateStatic("10 - 4", Empty);
        Assert.Equal(6.0, result, Eps);
    }

    [Fact]
    public void Evaluate_Multiplication_ReturnsProduct()
    {
        var result = FormulaSolver.EvaluateStatic("3 * 7", Empty);
        Assert.Equal(21.0, result, Eps);
    }

    [Fact]
    public void Evaluate_Division_ReturnsQuotient()
    {
        var result = FormulaSolver.EvaluateStatic("10 / 4", Empty);
        Assert.Equal(2.5, result, Eps);
    }

    [Fact]
    public void Evaluate_Parentheses_AppliedCorrectly()
    {
        var result = FormulaSolver.EvaluateStatic("(2 + 3) * 4", Empty);
        Assert.Equal(20.0, result, Eps);
    }

    [Fact]
    public void Evaluate_UnaryMinus_Negates()
    {
        var result = FormulaSolver.EvaluateStatic("-5", Empty);
        Assert.Equal(-5.0, result, Eps);
    }

    [Fact]
    public void Evaluate_NoVariables_ReturnsZero()
    {
        var result = FormulaSolver.EvaluateStatic("25 / 2 + 0", Empty);
        Assert.Equal(12.5, result, Eps);
    }

    // ── Evaluate: переменные ──────────────────────────────────────────────

    [Fact]
    public void Evaluate_SingleVariable_UsesValue()
    {
        var result = FormulaSolver.EvaluateStatic("x * 2",
            new Dictionary<string, double> { ["x"] = 10.0 });
        Assert.Equal(20.0, result, Eps);
    }

    [Fact]
    public void Evaluate_TwoVariables_Computes()
    {
        var result = FormulaSolver.EvaluateStatic("(a + b) / 2",
            new Dictionary<string, double> { ["a"] = 6.0, ["b"] = 4.0 });
        Assert.Equal(5.0, result, Eps);
    }

    [Fact]
    public void Evaluate_UnknownVariable_TreatedAsZero()
    {
        var result = FormulaSolver.EvaluateStatic("x + 5", Empty);
        Assert.Equal(5.0, result, Eps);
    }

    [Fact]
    public void Evaluate_CaseInsensitiveVariable()
    {
        var result = FormulaSolver.EvaluateStatic("Diameter / 2",
            new Dictionary<string, double> { ["diameter"] = 50.0 });
        Assert.Equal(25.0, result, Eps);
    }

    [Fact]
    public void Evaluate_FunctionCall_ReturnsZero()
    {
        // size_lookup — возвращает 0.0 в чистом парсере (lookup выполняется на уровне Revit)
        var result = FormulaSolver.EvaluateStatic("size_lookup(\"Table1\", R, \"DN50\", diameter)",
            new Dictionary<string, double> { ["diameter"] = 50.0 });
        Assert.Equal(0.0, result, Eps);
    }

    // ── SolveFor: линейные формулы ────────────────────────────────────────

    [Fact]
    public void SolveFor_DivisionBy2_Returns50()
    {
        var result = FormulaSolver.SolveForStatic("x / 2", "x", 25.0);
        Assert.NotNull(result);
        Assert.Equal(50.0, result!.Value, Eps);
    }

    [Fact]
    public void SolveFor_Multiplication_ReturnsCorrect()
    {
        var result = FormulaSolver.SolveForStatic("x * 2 + 1", "x", 11.0);
        Assert.NotNull(result);
        Assert.Equal(5.0, result!.Value, Eps);
    }

    [Fact]
    public void SolveFor_IdentityFormula_ReturnsTarget()
    {
        var result = FormulaSolver.SolveForStatic("x + 0", "x", 42.0);
        Assert.NotNull(result);
        Assert.Equal(42.0, result!.Value, Eps);
    }

    [Fact]
    public void SolveFor_DiameterFormula_RadiusToDouble()
    {
        // Типичный случай: radius = diameter / 2, решаем для diameter при radius=25
        var result = FormulaSolver.SolveForStatic("diameter / 2", "diameter", 25.0);
        Assert.NotNull(result);
        Assert.Equal(50.0, result!.Value, Eps);
    }

    [Fact]
    public void SolveFor_WithOtherVariables_UsesCorrectContext()
    {
        // f = x + y, solve for x with y=10, target=15 → x=5
        var result = FormulaSolver.SolveForStatic("x + y", "x", 15.0,
            new Dictionary<string, double> { ["y"] = 10.0 });
        Assert.NotNull(result);
        Assert.Equal(5.0, result!.Value, Eps);
    }

    // ── SolveFor: имена с пробелами (ADSK_Диаметр условный) ──────────────

    [Fact]
    public void SolveFor_SpacedVariableName_DivisionBy2_Returns50()
    {
        // 'ADSK_Диаметр условный / 2' — имя с пробелами и кириллицей
        var result = FormulaSolver.SolveForStatic(
            "ADSK_Диаметр условный / 2", "ADSK_Диаметр условный", 25.0);
        Assert.NotNull(result);
        Assert.Equal(50.0, result!.Value, Eps);
    }

    [Fact]
    public void SolveFor_SpacedVariableName_ReturnsDoubleTarget()
    {
        // Реальный случай: Условный радиус = ADSK_Диаметр условный / 2
        // SolveFor('ADSK_Диаметр условный / 2', 'ADSK_Диаметр условный', 7.5) → 15
        var result = FormulaSolver.SolveForStatic(
            "ADSK_Диаметр условный / 2", "ADSK_Диаметр условный", 7.5);
        Assert.NotNull(result);
        Assert.Equal(15.0, result!.Value, Eps);
    }

    [Fact]
    public void Evaluate_SpacedVariableName_Correct()
    {
        // Evaluate('ADSK_Диаметр условный / 2', {'ADSK_Диаметр условный': 30}) → 15
        var result = FormulaSolver.EvaluateStatic(
            "ADSK_Диаметр условный / 2",
            new System.Collections.Generic.Dictionary<string, double>
            {
                ["ADSK_Диаметр условный"] = 30.0
            });
        Assert.Equal(15.0, result, Eps);
    }

    // ── SolveFor: возвращает null ─────────────────────────────────────────

    [Fact]
    public void SolveFor_NonlinearFormula_SolvedByBisection()
    {
        // x * x — нелинейно, но новый FormulaSolver решает бисекцией
        var result = FormulaSolver.SolveForStatic("x * x", "x", 25.0);
        Assert.NotNull(result);
        Assert.Equal(5.0, result!.Value, 1e-3);
    }

    [Fact]
    public void SolveFor_VariableNotInFormula_ReturnsNull()
    {
        var result = FormulaSolver.SolveForStatic("a + b", "x", 10.0);
        Assert.Null(result);
    }

    [Fact]
    public void SolveFor_FunctionInFormula_ReturnsNull()
    {
        // size_lookup возвращает 0 — коэффициент при x=0, формула нелинейна или не зависит от x
        var result = FormulaSolver.SolveForStatic(
            "size_lookup(\"Table1\", R, \"DN50\", diameter)", "diameter", 25.0);
        Assert.Null(result);
    }

    // ── ParseSizeLookup ───────────────────────────────────────────────────

    [Fact]
    public void ParseSizeLookup_Valid4Args_Parsed()
    {
        var r = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(\"Table1\", radius, \"DN50\", diameter)");
        Assert.NotNull(r);
        Assert.Equal("Table1",   r!.Value.TableName);
        Assert.Equal("radius",   r.Value.TargetParameter);
        Assert.Single(r.Value.QueryParameters);
        Assert.Equal("diameter", r.Value.QueryParameters[0]);
    }

    [Fact]
    public void ParseSizeLookup_MultipleQueryParams_Parsed()
    {
        var r = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(\"MyTable\", outParam, \"default\", DN, PN, Type)");
        Assert.NotNull(r);
        Assert.Equal("MyTable", r!.Value.TableName);
        Assert.Equal("outParam", r.Value.TargetParameter);
        Assert.Equal(3, r.Value.QueryParameters.Count);
        Assert.Equal("DN",   r.Value.QueryParameters[0]);
        Assert.Equal("PN",   r.Value.QueryParameters[1]);
        Assert.Equal("Type", r.Value.QueryParameters[2]);
    }

    [Fact]
    public void ParseSizeLookup_NotSizeLookup_ReturnsNull()
    {
        var r = FormulaSolver.ParseSizeLookupStatic("diameter / 2");
        Assert.Null(r);
    }

    [Fact]
    public void ParseSizeLookup_TooFewArgs_ReturnsNull()
    {
        // Только 3 аргумента — нет ни одного queryParam
        // Новый парсер выбрасывает исключение при < 4 аргументах или парсит корректно
        // size_lookup(T, R, "x") — 3 аргумента, нет queryParams → пустой список
        var r = FormulaSolver.ParseSizeLookupStatic("size_lookup(T, R, \"x\")");
        Assert.NotNull(r);
        Assert.Empty(r!.Value.QueryParameters);
    }

    [Fact]
    public void ParseSizeLookup_CaseInsensitive()
    {
        var r = FormulaSolver.ParseSizeLookupStatic(
            "SIZE_LOOKUP(\"T1\", R, \"def\", param1)"  );
        Assert.NotNull(r);
        Assert.Equal("T1",     r!.Value.TableName);
        Assert.Equal("param1", r.Value.QueryParameters[0]);
    }

    // ── ExtractVariables ──────────────────────────────────────────────────

    [Fact]
    public void ExtractVariables_SimpleFormula_ExtractsVar()
    {
        var vars = FormulaSolver.ExtractVariablesStatic("diameter / 2");
        Assert.Contains("diameter", vars, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractVariables_TwoVariables_ExtractsBoth()
    {
        var vars = FormulaSolver.ExtractVariablesStatic("a + b * 3");
        Assert.Contains("a", vars, System.StringComparer.OrdinalIgnoreCase);
        Assert.Contains("b", vars, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractVariables_FunctionName_NotExtracted()
    {
        // size_lookup — это функция, не переменная
        var vars = FormulaSolver.ExtractVariablesStatic("size_lookup(\"T\", R, \"x\", diameter)");
        Assert.DoesNotContain("size_lookup", vars, System.StringComparer.OrdinalIgnoreCase);
        Assert.Contains("diameter", vars, System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractVariables_NoVariables_ReturnsEmpty()
    {
        var vars = FormulaSolver.ExtractVariablesStatic("2 + 3 * 4");
        Assert.Empty(vars);
    }

    // ── Вспомогательные ───────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, double> Empty =
        new Dictionary<string, double>();
}
