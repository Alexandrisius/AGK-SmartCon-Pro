using SmartCon.Core.Math;
using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Math;

public sealed class SizeRowSymbolMatcherTests
{
    private static SizeTableRow MakeRow(Dictionary<string, string> nonSizeValues)
    {
        return new SizeTableRow
        {
            TargetColumnIndex = 1,
            TargetRadiusFt = 0.1,
            ConnectorRadiiFt = new Dictionary<int, double> { [1] = 0.1 },
            NonSizeParameterValues = nonSizeValues
        };
    }

    private static (string, IReadOnlyDictionary<string, string>) MakeSymbol(
        string name, Dictionary<string, string> values)
    {
        return (name, values);
    }

    // ── Empty inputs ─────────────────────────────────────────────────────

    [Fact]
    public void MatchRowsToSymbols_EmptyInputs_ReturnsEmpty()
    {
        var result = SizeRowSymbolMatcher.MatchRowsToSymbols([], [], []);
        Assert.Empty(result);
    }

    [Fact]
    public void MatchRowsToSymbols_NoParams_ReturnsEmpty()
    {
        var row = MakeRow(new Dictionary<string, string> { ["P"] = "1.0" });
        var sym = MakeSymbol("A", new Dictionary<string, string> { ["P"] = "1.0" });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row], [sym], nonSizeTypeParams: []);

        Assert.Empty(result);
    }

    // ── Simple 1:1 matching ─────────────────────────────────────────────

    [Fact]
    public void MatchRowsToSymbols_OneRowOneSymbol_Matches()
    {
        var row = MakeRow(new Dictionary<string, string> { ["Исполнение"] = "1.000000" });
        var sym = MakeSymbol("Отвод", new Dictionary<string, string>
        {
            ["Исполнение"] = "1.000000"
        });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row], [sym], ["Исполнение"]);

        Assert.Single(result);
        Assert.Equal(["Отвод"], result[0]);
    }

    [Fact]
    public void MatchRowsToSymbols_ThreeRowsThreeSymbols_OneToOne()
    {
        var row1 = MakeRow(new Dictionary<string, string> { ["P"] = "1.0" });
        var row2 = MakeRow(new Dictionary<string, string> { ["P"] = "2.0" });
        var row3 = MakeRow(new Dictionary<string, string> { ["P"] = "3.0" });

        var symA = MakeSymbol("A", new Dictionary<string, string> { ["P"] = "1.0" });
        var symB = MakeSymbol("B", new Dictionary<string, string> { ["P"] = "2.0" });
        var symC = MakeSymbol("C", new Dictionary<string, string> { ["P"] = "3.0" });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row1, row2, row3], [symA, symB, symC], ["P"]);

        Assert.Equal(3, result.Count);
        Assert.Equal(["A"], result[0]);
        Assert.Equal(["B"], result[1]);
        Assert.Equal(["C"], result[2]);
    }

    // ── Multi-mapping: one row → several symbols with same value ────────

    [Fact]
    public void MatchRowsToSymbols_MultipleSymbolsWithSameValue_RowMatchesAll()
    {
        var row = MakeRow(new Dictionary<string, string> { ["Исполнение"] = "2.000000" });

        var sym1 = MakeSymbol("Исполнение 2",
            new Dictionary<string, string> { ["Исполнение"] = "2.000000" });
        var sym2 = MakeSymbol("Исполнение 2 — 108",
            new Dictionary<string, string> { ["Исполнение"] = "2.000000" });
        var sym3 = MakeSymbol("Отвод",
            new Dictionary<string, string> { ["Исполнение"] = "1.000000" });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row], [sym1, sym2, sym3], ["Исполнение"]);

        Assert.Single(result);
        Assert.Equal(2, result[0].Count);
        Assert.Contains("Исполнение 2", result[0]);
        Assert.Contains("Исполнение 2 — 108", result[0]);
        Assert.DoesNotContain("Отвод", result[0]);
    }

    // ── Orphan symbols: no CSV row matches → symbol excluded ────────────

    [Fact]
    public void FindOrphanSymbols_SymbolWithNoMatchingRow_IsOrphan()
    {
        var row1 = MakeRow(new Dictionary<string, string> { ["P"] = "1.0" });
        var row2 = MakeRow(new Dictionary<string, string> { ["P"] = "2.0" });

        var symA = MakeSymbol("A", new Dictionary<string, string> { ["P"] = "1.0" });
        var symB = MakeSymbol("B", new Dictionary<string, string> { ["P"] = "2.0" });
        var symOrphan = MakeSymbol("Orphan", new Dictionary<string, string> { ["P"] = "99.0" });

        var mapping = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row1, row2], [symA, symB, symOrphan], ["P"]);

        var orphans = SizeRowSymbolMatcher.FindOrphanSymbols(
            [symA, symB, symOrphan], mapping);

        Assert.Single(orphans);
        Assert.Equal("Orphan", orphans[0]);
    }

    [Fact]
    public void FindOrphanSymbols_AllSymbolsMatched_ReturnsEmpty()
    {
        var row1 = MakeRow(new Dictionary<string, string> { ["P"] = "1.0" });
        var symA = MakeSymbol("A", new Dictionary<string, string> { ["P"] = "1.0" });

        var mapping = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row1], [symA], ["P"]);

        var orphans = SizeRowSymbolMatcher.FindOrphanSymbols([symA], mapping);
        Assert.Empty(orphans);
    }

    // ── Numeric tolerance ───────────────────────────────────────────────

    [Fact]
    public void MatchRowsToSymbols_WithinTolerance_Matches()
    {
        // Δ=0.005 < NumericTolerance (0.01) → должно совпасть
        var row = MakeRow(new Dictionary<string, string> { ["P"] = "1.000000" });
        var sym = MakeSymbol("A", new Dictionary<string, string> { ["P"] = "1.005000" });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols([row], [sym], ["P"]);
        Assert.Single(result);
    }

    [Fact]
    public void MatchRowsToSymbols_BeyondTolerance_DoesNotMatch()
    {
        // Δ=0.5 > NumericTolerance (0.01) → не должно совпасть
        var row = MakeRow(new Dictionary<string, string> { ["P"] = "1.000000" });
        var sym = MakeSymbol("A", new Dictionary<string, string> { ["P"] = "1.500000" });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols([row], [sym], ["P"]);
        Assert.Empty(result);
    }

    // ── Unit-mismatch bug regression: feet vs mm for LENGTH ──────────────

    [Fact]
    public void MatchRowsToSymbols_UnitMismatch_InternalFeetVsDisplayMm_FailsToMatch()
    {
        // Regression: до фикса symbol value бралось в футах (0.003281 ft = 1.0 mm),
        // а CSV всегда в мм (1.000000). Это ломало маппинг. После фикса
        // RevitUnitsCompat конвертирует internal → display units.
        // Здесь моделируем старое (поломанное) поведение: CSV=1 mm, Symbol=0.003281 ft.
        var row = MakeRow(new Dictionary<string, string> { ["Исполнение"] = "1.000000" });
        var symBroken = MakeSymbol("Отвод_BrokenUnits",
            new Dictionary<string, string> { ["Исполнение"] = "0.003281" });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row], [symBroken], ["Исполнение"]);

        // Expected: unit mismatch → не совпадает (Δ=0.996773 ≫ 0.01)
        Assert.Empty(result);
    }

    [Fact]
    public void MatchRowsToSymbols_SameUnitsAfterCompat_Matches()
    {
        // После RevitUnitsCompat symbol value конвертируется из 0.003281 ft → 1.000000 mm
        // и теперь совпадает с CSV=1.000000. Моделируем правильное поведение.
        var row = MakeRow(new Dictionary<string, string> { ["Исполнение"] = "1.000000" });
        var symFixed = MakeSymbol("Отвод",
            new Dictionary<string, string> { ["Исполнение"] = "1.000000" });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row], [symFixed], ["Исполнение"]);

        Assert.Single(result);
        Assert.Equal(["Отвод"], result[0]);
    }

    // ── Missing parameter values ────────────────────────────────────────

    [Fact]
    public void MatchRowsToSymbols_RowMissingParam_DoesNotMatch()
    {
        var row = MakeRow(new Dictionary<string, string>());  // Нет значений
        var sym = MakeSymbol("A", new Dictionary<string, string> { ["P"] = "1.0" });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols([row], [sym], ["P"]);
        Assert.Empty(result);
    }

    [Fact]
    public void MatchRowsToSymbols_SymbolMissingParam_DoesNotMatch()
    {
        var row = MakeRow(new Dictionary<string, string> { ["P"] = "1.0" });
        var sym = MakeSymbol("A", new Dictionary<string, string>());  // Нет значений

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols([row], [sym], ["P"]);
        Assert.Empty(result);
    }

    // ── String comparison (non-numeric parameter values) ─────────────────

    [Fact]
    public void MatchRowsToSymbols_StringValues_CaseInsensitiveMatch()
    {
        var row = MakeRow(new Dictionary<string, string> { ["Material"] = "Сталь" });
        var sym = MakeSymbol("Type1",
            new Dictionary<string, string> { ["Material"] = "СТАЛЬ" });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row], [sym], ["Material"]);

        Assert.Single(result);
    }

    [Fact]
    public void MatchRowsToSymbols_StringValues_DifferentValuesDoNotMatch()
    {
        var row = MakeRow(new Dictionary<string, string> { ["Material"] = "Сталь" });
        var sym = MakeSymbol("Type1",
            new Dictionary<string, string> { ["Material"] = "Чугун" });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row], [sym], ["Material"]);

        Assert.Empty(result);
    }

    // ── Multi-param matching ────────────────────────────────────────────

    [Fact]
    public void MatchRowsToSymbols_MultipleParams_AllMustMatch()
    {
        var row = MakeRow(new Dictionary<string, string>
        {
            ["Исполнение"] = "2.000000",
            ["ТипСоед"] = "1"
        });

        var symOk = MakeSymbol("Ok", new Dictionary<string, string>
        {
            ["Исполнение"] = "2.000000",
            ["ТипСоед"] = "1"
        });

        var symPartial = MakeSymbol("Partial", new Dictionary<string, string>
        {
            ["Исполнение"] = "2.000000",
            ["ТипСоед"] = "2"  // mismatch
        });

        var result = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [row], [symOk, symPartial], ["Исполнение", "ТипСоед"]);

        Assert.Single(result);
        Assert.Equal(["Ok"], result[0]);
    }

    // ── Realistic ADSK case ─────────────────────────────────────────────

    [Fact]
    public void MatchRowsToSymbols_AdskElbowCase_ExcludesOrphans()
    {
        // Репро кейса из лога ADSK_СтальСварка_Отвод_ГОСТ17375-2001:
        //   Symbols:
        //     Отвод                          → Исполнение=1.0 mm
        //     Исполнение 1_Оцинкованные      → Исполнение=1.1 mm (orphan — в CSV нет)
        //     Исполнение 2                   → Исполнение=2.0 mm
        //     Исполнение 2 — 108             → Исполнение=2.0 mm (multi-match с Исп.2)
        //     Исполнение 2 — 102             → Исполнение=2.2 mm (orphan)
        //     Исполнение 2 — 114             → Исполнение=2.4 mm (orphan)
        //   CSV rows содержат Исполнение ∈ {1.0, 2.0} × различные DN
        var csvRow1 = MakeRow(new Dictionary<string, string> { ["Исполнение"] = "1.000000" });
        var csvRow2 = MakeRow(new Dictionary<string, string> { ["Исполнение"] = "2.000000" });

        var symbols = new List<(string, IReadOnlyDictionary<string, string>)>
        {
            ("Отвод",
                new Dictionary<string, string> { ["Исполнение"] = "1.000000" }),
            ("Исполнение 1_Оцинкованные",
                new Dictionary<string, string> { ["Исполнение"] = "1.100000" }),
            ("Исполнение 2",
                new Dictionary<string, string> { ["Исполнение"] = "2.000000" }),
            ("Исполнение 2 — 108",
                new Dictionary<string, string> { ["Исполнение"] = "2.000000" }),
            ("Исполнение 2 — 102",
                new Dictionary<string, string> { ["Исполнение"] = "2.200000" }),
            ("Исполнение 2 — 114",
                new Dictionary<string, string> { ["Исполнение"] = "2.400000" })
        };

        var mapping = SizeRowSymbolMatcher.MatchRowsToSymbols(
            [csvRow1, csvRow2], symbols, ["Исполнение"]);

        // Row 0 (Исполнение=1.0) → только "Отвод"
        Assert.Contains(0, mapping.Keys);
        Assert.Equal(["Отвод"], mapping[0]);

        // Row 1 (Исполнение=2.0) → "Исполнение 2" + "Исполнение 2 — 108"
        Assert.Contains(1, mapping.Keys);
        Assert.Equal(2, mapping[1].Count);
        Assert.Contains("Исполнение 2", mapping[1]);
        Assert.Contains("Исполнение 2 — 108", mapping[1]);

        // Orphan symbols: Исп.1_Оцинк., Исп.2—102, Исп.2—114
        var orphans = SizeRowSymbolMatcher.FindOrphanSymbols(symbols, mapping);
        Assert.Equal(3, orphans.Count);
        Assert.Contains("Исполнение 1_Оцинкованные", orphans);
        Assert.Contains("Исполнение 2 — 102", orphans);
        Assert.Contains("Исполнение 2 — 114", orphans);
    }
}
