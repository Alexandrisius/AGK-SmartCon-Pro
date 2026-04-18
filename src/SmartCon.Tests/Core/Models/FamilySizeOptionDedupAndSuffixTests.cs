using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

public sealed class FamilySizeOptionDedupAndSuffixTests
{
    private static double R(int dn) => FamilySizeFormatter.DnToRadiusFt(dn);

    private static FamilySizeOption MakeOption(
        string displayName,
        double radius,
        string? symbolName = null,
        Dictionary<int, double>? connectorRadii = null,
        Dictionary<string, string>? nonSizeValues = null)
    {
        return new FamilySizeOption
        {
            DisplayName = displayName,
            Radius = radius,
            TargetConnectorIndex = 0,
            AllConnectorRadii = connectorRadii ?? new Dictionary<int, double> { [0] = radius },
            SymbolName = symbolName,
            NonSizeParameterValues = nonSizeValues ?? new Dictionary<string, string>()
        };
    }

    // ── DeduplicateFamilyOptions ──────────────────────────────────────

    [Fact]
    public void Deduplicate_EmptyList_ReturnsEmpty()
    {
        var result = FamilySizeFormatter.DeduplicateFamilyOptions([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Deduplicate_AllUnique_ReturnsAll()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), "A"),
            MakeOption("DN 25", R(25), "B"),
        };

        var result = FamilySizeFormatter.DeduplicateFamilyOptions(options);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Deduplicate_SameRadiiSameSymbol_Deduplicates()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), "Отвод"),
            MakeOption("DN 50", R(50), "Отвод"),
        };

        var result = FamilySizeFormatter.DeduplicateFamilyOptions(options);
        Assert.Single(result);
    }

    [Fact]
    public void Deduplicate_SameRadiiDifferentSymbol_KeepsBoth()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), "Отвод"),
            MakeOption("DN 50", R(50), "Исполнение 2"),
        };

        var result = FamilySizeFormatter.DeduplicateFamilyOptions(options);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Deduplicate_SameRadiiOneWithSymbolOneWithout_KeepsBoth()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), null),
            MakeOption("DN 50", R(50), "Отвод"),
        };

        var result = FamilySizeFormatter.DeduplicateFamilyOptions(options);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Deduplicate_DifferentRadiiSameSymbol_KeepsBoth()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), "Отвод"),
            MakeOption("DN 25", R(25), "Отвод"),
        };

        var result = FamilySizeFormatter.DeduplicateFamilyOptions(options);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Deduplicate_DifferentNonSizeValues_KeepsBoth()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), null, nonSizeValues: new() { ["P"] = "1.0" }),
            MakeOption("DN 50", R(50), null, nonSizeValues: new() { ["P"] = "2.0" }),
        };

        var result = FamilySizeFormatter.DeduplicateFamilyOptions(options);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Deduplicate_SameNonSizeValues_Deduplicates()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), null, nonSizeValues: new() { ["P"] = "1.0" }),
            MakeOption("DN 50", R(50), null, nonSizeValues: new() { ["P"] = "1.0" }),
        };

        var result = FamilySizeFormatter.DeduplicateFamilyOptions(options);
        Assert.Single(result);
    }

    // ── AppendSymbolNameSuffix ────────────────────────────────────────

    [Fact]
    public void Suffix_EmptyList_ReturnsEmpty()
    {
        var result = FamilySizeFormatter.AppendSymbolNameSuffix([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Suffix_SingleOption_NoSuffix()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), "Отвод"),
        };

        var result = FamilySizeFormatter.AppendSymbolNameSuffix(options);
        Assert.Equal("DN 50", result[0].DisplayName);
    }

    [Fact]
    public void Suffix_AllDifferentDn_NoSuffix()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), "Отвод"),
            MakeOption("DN 25", R(25), "Исполнение 2"),
        };

        var result = FamilySizeFormatter.AppendSymbolNameSuffix(options);
        Assert.Equal("DN 50", result[0].DisplayName);
        Assert.Equal("DN 25", result[1].DisplayName);
    }

    [Fact]
    public void Suffix_DuplicateDnWithSymbols_AppendsSuffix()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), "Отвод"),
            MakeOption("DN 50", R(50), "Исполнение 2"),
        };

        var result = FamilySizeFormatter.AppendSymbolNameSuffix(options);
        Assert.Equal("DN 50 (Отвод)", result[0].DisplayName);
        Assert.Equal("DN 50 (Исполнение 2)", result[1].DisplayName);
    }

    [Fact]
    public void Suffix_DuplicateDnOneWithSymbolOneWithout_OnlySymbolGetsSuffix()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), null),
            MakeOption("DN 50", R(50), "Отвод"),
        };

        var result = FamilySizeFormatter.AppendSymbolNameSuffix(options);
        Assert.Equal("DN 50", result[0].DisplayName);
        Assert.Equal("DN 50 (Отвод)", result[1].DisplayName);
    }

    [Fact]
    public void Suffix_ThreeDuplicateDnAllWithSymbols_AppendsAll()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), "Отвод"),
            MakeOption("DN 50", R(50), "Исполнение 2"),
            MakeOption("DN 50", R(50), "Исполнение 2 — 108"),
        };

        var result = FamilySizeFormatter.AppendSymbolNameSuffix(options);
        Assert.Equal("DN 50 (Отвод)", result[0].DisplayName);
        Assert.Equal("DN 50 (Исполнение 2)", result[1].DisplayName);
        Assert.Equal("DN 50 (Исполнение 2 — 108)", result[2].DisplayName);
    }

    [Fact]
    public void Suffix_MixedDn_OnlyDuplicatesGetSuffix()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 25", R(25), "Отвод"),
            MakeOption("DN 50", R(50), "Отвод"),
            MakeOption("DN 50", R(50), "Исполнение 2"),
            MakeOption("DN 80", R(80), "Отвод"),
        };

        var result = FamilySizeFormatter.AppendSymbolNameSuffix(options);
        Assert.Equal("DN 25", result[0].DisplayName);
        Assert.Equal("DN 50 (Отвод)", result[1].DisplayName);
        Assert.Equal("DN 50 (Исполнение 2)", result[2].DisplayName);
        Assert.Equal("DN 80", result[3].DisplayName);
    }

    [Fact]
    public void Suffix_AllSameDnButNoSymbols_NoSuffix()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), null),
            MakeOption("DN 50", R(50), null),
        };

        var result = FamilySizeFormatter.AppendSymbolNameSuffix(options);
        Assert.Equal("DN 50", result[0].DisplayName);
        Assert.Equal("DN 50", result[1].DisplayName);
    }

    [Fact]
    public void Suffix_MultiConnectorDn_DifferentRadiiNoSuffix()
    {
        var options = new List<FamilySizeOption>
        {
            new()
            {
                DisplayName = "DN 50 × DN 25",
                Radius = R(50),
                TargetConnectorIndex = 0,
                AllConnectorRadii = new Dictionary<int, double> { [0] = R(50), [1] = R(25) },
                SymbolName = "Переход"
            },
            new()
            {
                DisplayName = "DN 65 × DN 50",
                Radius = R(65),
                TargetConnectorIndex = 0,
                AllConnectorRadii = new Dictionary<int, double> { [0] = R(65), [1] = R(50) },
                SymbolName = "Переход"
            },
        };

        var result = FamilySizeFormatter.AppendSymbolNameSuffix(options);
        Assert.Equal("DN 50 × DN 25", result[0].DisplayName);
        Assert.Equal("DN 65 × DN 50", result[1].DisplayName);
    }

    [Fact]
    public void Suffix_MultiConnectorSameDnDifferentSymbols_AppendsSuffix()
    {
        var options = new List<FamilySizeOption>
        {
            new()
            {
                DisplayName = "DN 25 × DN 20",
                Radius = R(25),
                TargetConnectorIndex = 0,
                AllConnectorRadii = new Dictionary<int, double> { [0] = R(25), [1] = R(20) },
                SymbolName = "KAN-therm"
            },
            new()
            {
                DisplayName = "DN 25 × DN 20",
                Radius = R(25),
                TargetConnectorIndex = 0,
                AllConnectorRadii = new Dictionary<int, double> { [0] = R(25), [1] = R(20) },
                SymbolName = "VALTEC"
            },
        };

        var result = FamilySizeFormatter.AppendSymbolNameSuffix(options);
        Assert.Equal("DN 25 × DN 20 (KAN-therm)", result[0].DisplayName);
        Assert.Equal("DN 25 × DN 20 (VALTEC)", result[1].DisplayName);
    }

    // ── Pipeline: Deduplicate → SortByTargetDn → AppendSymbolNameSuffix ──

    [Fact]
    public void Pipeline_FullFlow_DeduplicatesAndSuffixes()
    {
        var options = new List<FamilySizeOption>
        {
            MakeOption("DN 50", R(50), "Отвод"),
            MakeOption("DN 50", R(50), "Отвод"),
            MakeOption("DN 50", R(50), "Исполнение 2"),
            MakeOption("DN 25", R(25), "Отвод"),
            MakeOption("DN 25", R(25), "Исполнение 2"),
            MakeOption("DN 80", R(80), null),
        };

        var deduped = FamilySizeFormatter.DeduplicateFamilyOptions(options);
        Assert.Equal(5, deduped.Count);

        var sorted = deduped.OrderBy(o => FamilySizeFormatter.ToDn(o.Radius)).ToList();
        var result = FamilySizeFormatter.AppendSymbolNameSuffix(sorted);

        Assert.Equal(5, result.Count);
        Assert.Equal("DN 25 (Отвод)", result[0].DisplayName);
        Assert.Equal("DN 25 (Исполнение 2)", result[1].DisplayName);
        Assert.Equal("DN 50 (Отвод)", result[2].DisplayName);
        Assert.Equal("DN 50 (Исполнение 2)", result[3].DisplayName);
        Assert.Equal("DN 80", result[4].DisplayName);
    }
}
