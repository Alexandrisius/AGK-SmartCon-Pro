using SmartCon.Core.Math.FormulaEngine;
using SmartCon.Core.Math.FormulaEngine.Solver;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public class SizeLookupParserTests
{
    private static readonly FormulaSolver Solver = new();

    [Fact]
    public void ParseSizeLookup_Direct()
    {
        var (table, parms) = Solver.ParseSizeLookup(
            "size_lookup(\"Table1\", radius, \"DN50\", diameter)");
        Assert.Equal("Table1", table);
        Assert.Single(parms);
        Assert.Equal("diameter", parms[0]);
    }

    [Fact]
    public void ParseSizeLookup_MultipleQueryParams()
    {
        var (table, parms) = Solver.ParseSizeLookup(
            "size_lookup(\"T\", out, \"def\", DN, PN, Type)");
        Assert.Equal("T", table);
        Assert.Equal(3, parms.Count);
        Assert.Equal("DN", parms[0]);
        Assert.Equal("PN", parms[1]);
        Assert.Equal("Type", parms[2]);
    }

    [Fact]
    public void ParseSizeLookup_InsideIf()
    {
        var (table, parms) = Solver.ParseSizeLookup(
            "if(x > 0, size_lookup(\"MyTable\", R, \"d\", DN), 0)");
        Assert.Equal("MyTable", table);
        Assert.Single(parms);
        Assert.Equal("DN", parms[0]);
    }

    [Fact]
    public void ParseSizeLookup_NotFound_Throws()
    {
        Assert.Throws<FormulaParseException>(
            () => Solver.ParseSizeLookup("diameter / 2"));
    }

    [Fact]
    public void ParseSizeLookup_IdentifierTableName()
    {
        var (table, parms) = Solver.ParseSizeLookup(
            "size_lookup(BP_LookupTable, param, \"def\", DN)");
        Assert.Equal("BP_LookupTable", table);
    }

    [Fact]
    public void ParseSizeLookup_Static_ReturnsNull_WhenNotFound()
    {
        Assert.Null(FormulaSolver.ParseSizeLookupStatic("x + 5"));
    }

    [Fact]
    public void ParseSizeLookup_Static_ReturnsCorrectResult()
    {
        var result = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(\"T\", p, \"d\", DN, PN)");
        Assert.NotNull(result);
        Assert.Equal("T", result.Value.TableName);
        Assert.Equal("p", result.Value.TargetParameter);
        Assert.Equal(2, result.Value.QueryParameters.Count);
    }

    [Fact]
    public void ParseSizeLookup_CaseInsensitive()
    {
        var result = FormulaSolver.ParseSizeLookupStatic(
            "SIZE_LOOKUP(\"Tab\", p, \"d\", DN)");
        Assert.NotNull(result);
        Assert.Equal("Tab", result.Value.TableName);
    }

    [Fact]
    public void ParseSizeLookup_NestedInIf_Static()
    {
        var result = FormulaSolver.ParseSizeLookupStatic(
            "if(cond > 0, size_lookup(\"Deep\", out, \"def\", A, B), 0)");
        Assert.NotNull(result);
        Assert.Equal("Deep", result.Value.TableName);
        Assert.Equal(2, result.Value.QueryParameters.Count);
    }

    // ── Имена с пробелами (регрессия DN dropdown) ───────────────────────

    [Fact]
    public void ParseSizeLookup_SpacedTargetParam()
    {
        // Реальный Revit-кейс: size_lookup(DN_Table, Условный радиус, "default", ADSK_Диаметр условный)
        var result = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(DN_Table, Условный радиус, \"default\", ADSK_Диаметр условный)");
        Assert.NotNull(result);
        Assert.Equal("DN_Table", result!.Value.TableName);
        Assert.Equal("Условный радиус", result.Value.TargetParameter);
        Assert.Single(result.Value.QueryParameters);
        Assert.Equal("ADSK_Диаметр условный", result.Value.QueryParameters[0]);
    }

    [Fact]
    public void ParseSizeLookup_SpacedTableName()
    {
        var result = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(My Table, R, \"def\", DN)");
        Assert.NotNull(result);
        Assert.Equal("My Table", result!.Value.TableName);
    }

    [Fact]
    public void ParseSizeLookup_MultipleSpacedQueryParams()
    {
        var result = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(T, Выход радиус, \"def\", Параметр один, Параметр два)");
        Assert.NotNull(result);
        Assert.Equal("Выход радиус", result!.Value.TargetParameter);
        Assert.Equal(2, result.Value.QueryParameters.Count);
        Assert.Equal("Параметр один", result.Value.QueryParameters[0]);
        Assert.Equal("Параметр два", result.Value.QueryParameters[1]);
    }

    [Fact]
    public void ParseSizeLookup_SpacedInsideIf()
    {
        var result = FormulaSolver.ParseSizeLookupStatic(
            "if(x > 0, size_lookup(DN_Table, Условный радиус, \"def\", ADSK_Диаметр условный), 0)");
        Assert.NotNull(result);
        Assert.Equal("DN_Table", result!.Value.TableName);
        Assert.Equal("Условный радиус", result.Value.TargetParameter);
        Assert.Equal("ADSK_Диаметр условный", result.Value.QueryParameters[0]);
    }

    // ── Числовой default value (реальные Revit-формулы) ─────────────────

    [Fact]
    public void ParseSizeLookup_NumericDefault_Integer()
    {
        // size_lookup(BP_LookupTable, "p3", 22 мм, BP_NominalDiameter) → после UnitStripper "22"
        var result = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(BP_LookupTable, \"p3\", 22, BP_NominalDiameter)");
        Assert.NotNull(result);
        Assert.Equal("BP_LookupTable", result!.Value.TableName);
        Assert.Equal("p3", result.Value.TargetParameter);
        Assert.Single(result.Value.QueryParameters);
        Assert.Equal("BP_NominalDiameter", result.Value.QueryParameters[0]);
    }

    [Fact]
    public void ParseSizeLookup_NumericDefault_Zero()
    {
        // size_lookup(BP_LookupTable, "Mass", 0, BP_NominalDiameter)
        var result = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(BP_LookupTable, \"Mass\", 0, BP_NominalDiameter)");
        Assert.NotNull(result);
        Assert.Equal("Mass", result!.Value.TargetParameter);
    }

    [Fact]
    public void ParseSizeLookup_NumericDefault_Decimal()
    {
        // size_lookup(BP_LookupTable, "с1", 33.5 мм, BP_NominalDiameter) → "33.5"
        var result = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(BP_LookupTable, \"с1\", 33.5, BP_NominalDiameter)");
        Assert.NotNull(result);
        Assert.Equal("с1", result!.Value.TargetParameter);
    }

    [Fact]
    public void ParseSizeLookup_NumericDefault_One()
    {
        // size_lookup(BP_LookupTable, "BP_Kv", 1, BP_NominalDiameter)
        var result = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(BP_LookupTable, \"BP_Kv\", 1, BP_NominalDiameter)");
        Assert.NotNull(result);
        Assert.Equal("BP_Kv", result!.Value.TargetParameter);
    }

    [Fact]
    public void ParseSizeLookup_NumericDefault_MultiQuery()
    {
        // size_lookup(BP_LookupTable, "A", 0, BP_NominalDiameter_2, BP_NominalDiameter)
        var result = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(BP_LookupTable, \"A\", 0, BP_NominalDiameter_2, BP_NominalDiameter)");
        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.QueryParameters.Count);
        Assert.Equal("BP_NominalDiameter_2", result.Value.QueryParameters[0]);
        Assert.Equal("BP_NominalDiameter", result.Value.QueryParameters[1]);
    }

    [Fact]
    public void ParseSizeLookup_NumericDefault_LargeValue()
    {
        // size_lookup(BP_LookupTable, "A", 217, BP_NominalDiameter)
        var result = FormulaSolver.ParseSizeLookupStatic(
            "size_lookup(BP_LookupTable, \"A\", 217, BP_NominalDiameter)");
        Assert.NotNull(result);
        Assert.Equal("A", result!.Value.TargetParameter);
        Assert.Equal("BP_NominalDiameter", result.Value.QueryParameters[0]);
    }
}
