using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// The string logic behind map_family_parameters mode=formula_repointed:
/// the ФОП-migration re-authors formulas over the TARGET parameter names
/// instead of chaining duplicates to the sources that get deleted later.
/// </summary>
public sealed class AgentFormulaRepointerTests
{
    private static readonly KeyValuePair<string, string>[] Pairs =
    {
        new("ADSK_Марка", "KRTRS_Марка"),
        new("ADSK_Наименование", "KRTRS_Наименование"),
        new("ADSK_Наименование краткое", "KRTRS_Наименование краткое"),
        new("ADSK_Диаметр условный", "KRTRS_Диаметр условный"),
    };

    [Fact]
    public void Repoint_ReplacesParameterReferences()
    {
        var result = AgentFormulaRepointer.Repoint(
            "if(ADSK_Марка = \"ГОСТ\", ADSK_Наименование краткое, ADSK_Наименование)",
            Pairs,
            out var replaced);

        Assert.DoesNotContain("ADSK_", result);
        Assert.Contains("KRTRS_Марка", result);
        Assert.Contains("KRTRS_Наименование краткое", result);
        Assert.Contains("KRTRS_Наименование)", result);
        // Quoted literals are data: a size_lookup column or text value keeps
        // its original spelling even when it collides with a parameter name.
        Assert.Contains("\"ГОСТ\"", result);
        Assert.Equal(3, replaced.Count);
    }

    [Fact]
    public void Repoint_LongestNameWinsOverItsPrefix()
    {
        var result = AgentFormulaRepointer.Repoint(
            "ADSK_Наименование краткое + \" - \" + ADSK_Наименование",
            Pairs,
            out _);

        Assert.Contains("KRTRS_Наименование краткое", result);
        // The short rule must not leave «краткое» orphaned by replacing inside
        // the long name (longest-first ordering).
        Assert.Equal("KRTRS_Наименование краткое + \" - \" + KRTRS_Наименование", result);
    }

    [Fact]
    public void Repoint_RespectsIdentifierBoundaries()
    {
        var result = AgentFormulaRepointer.Repoint(
            "ADSK_Диаметр условный * 2 - ADSK_Диаметр условный3",
            new[] { new KeyValuePair<string, string>("ADSK_Диаметр условный", "KRTRS_Диаметр условный") },
            out _);

        // «…условный3» is a different identifier: the boundary guard keeps it.
        Assert.Equal("KRTRS_Диаметр условный * 2 - ADSK_Диаметр условный3", result);
    }

    [Fact]
    public void Repoint_IsCaseInsensitive()
    {
        var result = AgentFormulaRepointer.Repoint(
            "adsk_марка",
            new[] { new KeyValuePair<string, string>("ADSK_Марка", "KRTRS_Марка") },
            out var replaced);

        Assert.Equal("KRTRS_Марка", result);
        Assert.Single(replaced);
    }

    [Fact]
    public void Repoint_KeepsLiteralOnlyLockIntact()
    {
        // «Единица измерения = "шт."» — a quoted-literal lock. Repointing has
        // nothing to rewrite; SetFormula of the same literal restores the lock.
        var result = AgentFormulaRepointer.Repoint(
            "\"шт.\"",
            Pairs,
            out var replaced);

        Assert.Equal("\"шт.\"", result);
        Assert.Empty(replaced);
    }

    [Fact]
    public void Repoint_SizeLookupKeepsTableAndColumnLiterals()
    {
        // Live migration feedback: "lookup tables do not transfer with map!" —
        // they do: the table name and the column stay QUOTED LITERALS (data),
        // only the parameter reference inside size_lookup is re-authored, and
        // the tables live in the family document untouched.
        var result = AgentFormulaRepointer.Repoint(
            "size_lookup(\"LT_Массы\", \"Масса\", 0 kg, ADSK_Диаметр условный)",
            Pairs,
            out var replaced);

        Assert.Equal(
            "size_lookup(\"LT_Массы\", \"Масса\", 0 kg, KRTRS_Диаметр условный)",
            result);
        Assert.Single(replaced);
    }

    [Fact]
    public void WrapWithUnit_UsesExplicitConstants()
    {
        // Autodesk: divide by an explicit unit to strip the dimension, multiply
        // to add it back; a bare 1 makes Revit guess.
        Assert.Equal("(X * 2) / 1 mm", AgentFormulaRepointer.WrapWithUnit("X * 2", "mm", addUnits: false));
        Assert.Equal("(X * 2) * 1 mm", AgentFormulaRepointer.WrapWithUnit("X * 2", "mm", addUnits: true));
        Assert.True(AgentFormulaRepointer.IsValidUnitToken("m2"));
        Assert.True(AgentFormulaRepointer.IsValidUnitToken("°C"));
        Assert.False(AgentFormulaRepointer.IsValidUnitToken("1 mm"));
        Assert.False(AgentFormulaRepointer.IsValidUnitToken("\"mm\""));
    }
}
