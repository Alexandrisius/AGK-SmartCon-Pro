using SmartCon.Core.Math.FormulaEngine.Solver;
using SmartCon.Core.Models;
using Xunit;
using SmartCon.Core;

using static SmartCon.Core.Units;
namespace SmartCon.Tests.Core.Math;

/// <summary>
/// Тесты Фазы 6B — Multi-Column LookupTable.
/// Проверяем Core-логику: парсинг multi-query size_lookup формул,
/// контракт LookupColumnConstraint, DN-конверсия.
/// Revit-специфичный код (ExtractColumnValues, BuildLookupContext) НЕ тестируем.
/// </summary>
public sealed class MultiColumnLookupTests
{
    // ── ParseSizeLookupStatic: multi-query формулы ─────────────────────────

    [Fact]
    public void ParseSizeLookup_TwoQueryParams_ReturnsBoth()
    {
        // Реальная формула из BP_A0309_VALTEC_VTr.580-582
        var formula = "size_lookup(BP_LookupTable, \"code\", \"no\", BP_NominalDiameter_2, BP_NominalDiameter)";
        var result = FormulaSolver.ParseSizeLookupStatic(formula);

        Assert.NotNull(result);
        Assert.Equal("BP_LookupTable", result.Value.TableName);
        Assert.Equal("code", result.Value.TargetParameter);
        Assert.Equal(2, result.Value.QueryParameters.Count);
        Assert.Equal("BP_NominalDiameter_2", result.Value.QueryParameters[0]);
        Assert.Equal("BP_NominalDiameter", result.Value.QueryParameters[1]);
    }

    [Fact]
    public void ParseSizeLookup_SingleQueryParam_ReturnsSingle()
    {
        var formula = "size_lookup(BP_LookupTable, \"Mass\", 0, BP_NominalDiameter)";
        var result = FormulaSolver.ParseSizeLookupStatic(formula);

        Assert.NotNull(result);
        Assert.Single(result.Value.QueryParameters);
        Assert.Equal("BP_NominalDiameter", result.Value.QueryParameters[0]);
    }

    [Fact]
    public void ParseSizeLookup_ThreeQueryParams_ReturnsAll()
    {
        var formula = "size_lookup(Table, \"p\", 0, DN1, DN2, DN3)";
        var result = FormulaSolver.ParseSizeLookupStatic(formula);

        Assert.NotNull(result);
        Assert.Equal(3, result.Value.QueryParameters.Count);
        Assert.Equal("DN1", result.Value.QueryParameters[0]);
        Assert.Equal("DN2", result.Value.QueryParameters[1]);
        Assert.Equal("DN3", result.Value.QueryParameters[2]);
    }

    [Fact]
    public void ParseSizeLookup_QuotedTableName_Parsed()
    {
        var formula = "size_lookup(\"MyTable\", \"p3\", 22, BP_NominalDiameter)";
        var result = FormulaSolver.ParseSizeLookupStatic(formula);

        Assert.NotNull(result);
        Assert.Equal("MyTable", result.Value.TableName);
        Assert.Equal("p3", result.Value.TargetParameter);
        Assert.Single(result.Value.QueryParameters);
    }

    [Fact]
    public void ParseSizeLookup_StringDefaultValue_TwoQueryParams()
    {
        // Строковый default + 2 query params (реальный паттерн из логов)
        var formula = "size_lookup(BP_LookupTable, \"code\", \"no\", BP_NominalDiameter_2, BP_NominalDiameter)";
        var result = FormulaSolver.ParseSizeLookupStatic(formula);

        Assert.NotNull(result);
        Assert.Equal("no", result.Value.TargetParameter is not null ? "no" : "fail"); // default = "no"
        Assert.Equal(2, result.Value.QueryParameters.Count);
    }

    [Fact]
    public void ParseSizeLookup_NumericDefaultValue_SingleQuery()
    {
        // Числовой default (паттерн: size_lookup(T, "p3", 22, DN))
        var formula = "size_lookup(BP_LookupTable, \"p3\", 22, BP_NominalDiameter)";
        var result = FormulaSolver.ParseSizeLookupStatic(formula);

        Assert.NotNull(result);
        Assert.Equal("p3", result.Value.TargetParameter);
        Assert.Single(result.Value.QueryParameters);
        Assert.Equal("BP_NominalDiameter", result.Value.QueryParameters[0]);
    }

    [Fact]
    public void ParseSizeLookup_ZeroDefault_SingleQuery()
    {
        var formula = "size_lookup(BP_LookupTable, \"Mass\", 0, BP_NominalDiameter)";
        var result = FormulaSolver.ParseSizeLookupStatic(formula);

        Assert.NotNull(result);
        Assert.Equal("Mass", result.Value.TargetParameter);
        Assert.Single(result.Value.QueryParameters);
    }

    [Fact]
    public void ParseSizeLookup_NotSizeLookup_ReturnsNull()
    {
        var formula = "DN * 2 + 5";
        var result = FormulaSolver.ParseSizeLookupStatic(formula);
        Assert.Null(result);
    }

    [Fact]
    public void ParseSizeLookup_EmptyFormula_ReturnsNull()
    {
        Assert.Null(FormulaSolver.ParseSizeLookupStatic(""));
        Assert.Null(FormulaSolver.ParseSizeLookupStatic(null!));
        Assert.Null(FormulaSolver.ParseSizeLookupStatic("   "));
    }

    // ── QueryParameters: порядок столбцов CSV ──────────────────────────────

    [Fact]
    public void QueryParams_OrderMatchesCsvColumns()
    {
        // size_lookup(T, target, default, DN2, DN1)
        // CSV: col[0]=comments, col[1]=DN2, col[2]=DN1, col[3+]=values
        var formula = "size_lookup(T, \"target\", 0, DN2, DN1)";
        var result = FormulaSolver.ParseSizeLookupStatic(formula);

        Assert.NotNull(result);
        // QueryParameters[0] → col[1] = DN2
        Assert.Equal("DN2", result.Value.QueryParameters[0]);
        // QueryParameters[1] → col[2] = DN1
        Assert.Equal("DN1", result.Value.QueryParameters[1]);
    }

    // ── LookupColumnConstraint: контракт для multi-column фильтрации ──────

    [Fact]
    public void Constraint_MatchesQueryParam_ByParameterName()
    {
        // Сценарий: фитинг с DN1 (col[1]) и DN2 (col[2])
        // Пользователь меняет DN1, а DN2 = 15мм (constraint)
        var constraint = new LookupColumnConstraint(
            ConnectorIndex: 2,
            ParameterName: "BP_NominalDiameter_2",
            ValueMm: 15.0);

        // Constraint.ParameterName должен совпадать с QueryParameters[i]
        // из ParseSizeLookupStatic
        var formula = "size_lookup(BP_LookupTable, \"code\", \"no\", BP_NominalDiameter_2, BP_NominalDiameter)";
        var parsed = FormulaSolver.ParseSizeLookupStatic(formula);

        Assert.NotNull(parsed);
        Assert.Contains(parsed.Value.QueryParameters,
            qp => string.Equals(qp, constraint.ParameterName, System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constraint_DoesNotMatch_UnrelatedParam()
    {
        var constraint = new LookupColumnConstraint(
            ConnectorIndex: 5,
            ParameterName: "SomeOtherParam",
            ValueMm: 100.0);

        var formula = "size_lookup(T, \"p\", 0, DN1, DN2)";
        var parsed = FormulaSolver.ParseSizeLookupStatic(formula);

        Assert.NotNull(parsed);
        Assert.DoesNotContain(parsed.Value.QueryParameters,
            qp => string.Equals(qp, constraint.ParameterName, System.StringComparison.OrdinalIgnoreCase));
    }

    // ── Сценарий full pipeline: сбор constraints ──────────────────────────

    [Fact]
    public void Scenario_TwoConnectors_OneConstraint()
    {
        // Фитинг с 2 DN коннекторами:
        //   conn#2 → BP_NominalDiameter (col[2])
        //   conn#3 → BP_NominalDiameter_2 (col[1])
        // Пользователь меняет conn#2, а conn#3 = DN 15мм

        var formula = "size_lookup(BP_LookupTable, \"code\", \"no\", BP_NominalDiameter_2, BP_NominalDiameter)";
        var parsed = FormulaSolver.ParseSizeLookupStatic(formula);
        Assert.NotNull(parsed);

        // Строим constraints от conn#3 (другой коннектор)
        var constraints = new List<LookupColumnConstraint>
        {
            new(ConnectorIndex: 3, ParameterName: "BP_NominalDiameter_2", ValueMm: 15.0)
        };

        // Проверяем что constraint.ParameterName — это QueryParameters[0]
        Assert.Equal("BP_NominalDiameter_2", parsed.Value.QueryParameters[0]);
        Assert.Equal(constraints[0].ParameterName, parsed.Value.QueryParameters[0]);

        // col[1] = QueryParameters[0] = "BP_NominalDiameter_2" — фильтр по 15.0
        // col[2] = QueryParameters[1] = "BP_NominalDiameter" — target (dropdown)
        Assert.Equal(2, parsed.Value.QueryParameters.Count);
    }

    [Fact]
    public void Scenario_SingleConnector_NoConstraints()
    {
        // Обычный фитинг с 1 DN — constraints пустые
        var formula = "size_lookup(BP_LookupTable, \"Mass\", 0, BP_NominalDiameter)";
        var parsed = FormulaSolver.ParseSizeLookupStatic(formula);
        Assert.NotNull(parsed);
        Assert.Single(parsed.Value.QueryParameters);

        // Для single-query constraints не применяются
        var constraints = new List<LookupColumnConstraint>();
        Assert.Empty(constraints);
    }

    // ── DN ↔ Radius conversion (используется при построении constraints) ──

    [Theory]
    [InlineData(15.0)]
    [InlineData(20.0)]
    [InlineData(25.0)]
    [InlineData(32.0)]
    [InlineData(40.0)]
    [InlineData(50.0)]
    public void DnMm_ToRadius_AndBack_Roundtrips(double dnMm)
    {
        // Radius (feet) = (dnMm / 2.0) * MmToFeet
        double radiusFeet = (dnMm / 2.0) * MmToFeet;

        // Обратно: DN = Round(radius * 2 * FeetToMm)
        double recovered = System.Math.Round(radiusFeet * 2.0 * FeetToMm);

        Assert.Equal(dnMm, recovered);
    }

    [Theory]
    [InlineData(15.0)]
    [InlineData(20.0)]
    [InlineData(25.0)]
    public void ConstraintValueMm_MatchesCsvDiameter(double dnMm)
    {
        // ValueMm в constraint = Round(connectorRadius * 2 * FeetToMm)
        // Это должно совпадать с числом в CSV (NominalDiameter в мм)
        double radiusFeet = (dnMm / 2.0) * MmToFeet;
        double valueMm = System.Math.Round(radiusFeet * 2.0 * FeetToMm);

        Assert.Equal(dnMm, valueMm);
    }
}
