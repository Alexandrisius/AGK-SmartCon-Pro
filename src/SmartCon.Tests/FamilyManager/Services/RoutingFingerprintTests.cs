using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// ADR-072 World B: <see cref="RoutingFingerprint"/> is the routing equality
/// probe (sync / stale / placement) after routing left the content hash at
/// FHV20. The canonical string is byte-identical to the pre-FHV20 ROUTING
/// section substring, so fingerprints stay comparable across the migration.
/// </summary>
public sealed class RoutingFingerprintTests
{
    [Fact]
    public void Compute_NullRouting_ReturnsNull()
    {
        Assert.Null(RoutingFingerprint.Compute(null));
    }

    [Fact]
    public void Compute_SameRulesSameOrder_EqualFingerprints()
    {
        var a = RoutingFingerprint.Compute(MakeRouting());
        var b = RoutingFingerprint.Compute(MakeRouting());
        Assert.NotNull(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_RuleOrderIsContent_DifferentFingerprints()
    {
        var straight = RoutingFingerprint.Compute(MakeRouting());
        var swapped = RoutingFingerprint.Compute(new RoutingPreferencesSnapshot(1,
        [
            new RoutingRuleSnapshot(1, "B:Т", "b", []),
            new RoutingRuleSnapshot(1, "A:С", "a", []),
        ]));
        Assert.NotEqual(straight, swapped);
    }

    [Fact]
    public void Compute_PartOrCriteriaChange_ShiftsFingerprint()
    {
        var baseline = RoutingFingerprint.Compute(MakeRouting());
        var otherPart = RoutingFingerprint.Compute(new RoutingPreferencesSnapshot(1,
        [
            new RoutingRuleSnapshot(0, "Seg-1", "seg", []),
            new RoutingRuleSnapshot(1, "A:Другой", "a", []),
        ]));
        var otherCriteria = RoutingFingerprint.Compute(new RoutingPreferencesSnapshot(1,
        [
            new RoutingRuleSnapshot(0, "Seg-1", "seg", []),
            new RoutingRuleSnapshot(1, "A:С", "a",
                [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.1, 0.9)]),
        ]));
        Assert.NotEqual(baseline, otherPart);
        Assert.NotEqual(baseline, otherCriteria);
    }

    [Fact]
    public void Compute_ParamGroupKey_RoundtripsCanonicalToken()
    {
        var param = RoutingFingerprint.Compute(new RoutingPreferencesSnapshot(0,
        [
            new RoutingRuleSnapshot(RoutingGroupKeys.ParamGroupType, null, "",
                [], GroupKey: RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM")),
        ]));
        var otherParam = RoutingFingerprint.Compute(new RoutingPreferencesSnapshot(0,
        [
            new RoutingRuleSnapshot(RoutingGroupKeys.ParamGroupType, null, "",
                [], GroupKey: RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_CROSS_PARAM")),
        ]));
        Assert.NotNull(param);
        Assert.NotEqual(param, otherParam);
    }

    [Fact]
    public void WithoutSizeCriteria_Null_ReturnsNull()
    {
        Assert.Null(RoutingFingerprint.WithoutSizeCriteria(null));
    }

    [Fact]
    public void WithoutSizeCriteria_StripsSizeCriteria_KeepsRuleOrderAndOthers()
    {
        var normalized = RoutingFingerprint.WithoutSizeCriteria(new RoutingPreferencesSnapshot(1,
        [
            new RoutingRuleSnapshot(1, "A:С", "a",
            [
                new RoutingCriterionSnapshot(RoutingFingerprint.PrimarySizeCriterionType, 0.1, 0.9),
                new RoutingCriterionSnapshot("SlopeCriterion", 0, 0),
            ]),
            new RoutingRuleSnapshot(1, "B:Т", "b", []),
        ]));

        Assert.NotNull(normalized);
        Assert.Equal(2, normalized!.Rules.Count);
        Assert.Equal("A:С", normalized.Rules[0].PartName);
        Assert.Equal("B:Т", normalized.Rules[1].PartName);
        var remaining = Assert.Single(normalized.Rules[0].Criteria);
        Assert.Equal("SlopeCriterion", remaining.CriterionType);
    }

    [Fact]
    public void WithoutSizeCriteria_NoCriteria_SameFingerprint()
    {
        var routing = MakeRouting();
        var normalized = RoutingFingerprint.WithoutSizeCriteria(routing);
        Assert.Equal(RoutingFingerprint.Compute(routing), RoutingFingerprint.Compute(normalized));
    }

    [Fact]
    public void WithoutSizeCriteria_LegacyCriteria_MatchesCriteriaFreeSnapshot()
    {
        // Owner decision 2026-08-30: legacy DB rows stored the size
        // criterion for ducts; live extraction no longer reports it — the
        // normalization must make the two sides compare equal.
        var legacy = RoutingFingerprint.WithoutSizeCriteria(new RoutingPreferencesSnapshot(1,
        [
            new RoutingRuleSnapshot(1, "A:С", "a",
                [new RoutingCriterionSnapshot(RoutingFingerprint.PrimarySizeCriterionType, 0, 10000)]),
        ]));
        var modern = new RoutingPreferencesSnapshot(1,
        [
            new RoutingRuleSnapshot(1, "A:С", "a", []),
        ]);
        Assert.Equal(RoutingFingerprint.Compute(modern), RoutingFingerprint.Compute(legacy));
    }

    [Fact]
    public void CanonicalSubstring_GoldenLiteral_ByteIdenticalToPreFhv20RoutingSection()
    {
        // The World B invariant: the canonical string MUST stay byte-identical
        // to the pre-FHV20 ROUTING section substring, otherwise every stored
        // fingerprint compares drifted after the migration. Escaping: '|' →
        // %7C, '%' → %25 (':' is NOT escaped); no-part → NOPART; criteria as
        // 0.###### InvariantCulture triples; trailing separators are content.
        var routing = new RoutingPreferencesSnapshot(1,
        [
            new RoutingRuleSnapshot(0, "Seg-1", "сегмент", []),
            new RoutingRuleSnapshot(1, "A|B:С", "50%",
                [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.15, 1.5)]),
            new RoutingRuleSnapshot(RoutingGroupKeys.ParamGroupType, null, "",
                [], GroupKey: RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM")),
        ]);

        Assert.Equal(
            "ROUTING|1|0|Seg-1|сегмент|1|A%7CB:С|50%25|PrimarySizeCriterion|0.15|1.5|" +
            "Param:RBS_CURVETYPE_DEFAULT_TEE_PARAM|NOPART||",
            RoutingFingerprint.CanonicalSubstring(routing));
    }

    [Fact]
    public void CanonicalSubstring_NullRouting_CanonicalEmptyMarker()
    {
        Assert.Equal("ROUTING|-|", RoutingFingerprint.CanonicalSubstring(null));
    }

    [Fact]
    public void CanonicalSubstring_ParserRoundTrip_Stable()
    {
        // RoutingSectionParser (file-free backfill source) must recover a
        // snapshot whose canonical form is identical to the original.
        var routing = new RoutingPreferencesSnapshot(1,
        [
            new RoutingRuleSnapshot(0, "Seg-1", "сегмент", []),
            new RoutingRuleSnapshot(1, "A|B:С", "50%",
                [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.15, 1.5)]),
            new RoutingRuleSnapshot(RoutingGroupKeys.ParamGroupType, null, "",
                [], GroupKey: RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM")),
        ]);
        var canonical = RoutingFingerprint.CanonicalSubstring(routing);

        var parsed = RoutingSectionParser.Parse(new Dictionary<string, string>
        {
            ["ROUTING|TypeA"] = canonical,
            ["FAMKEY|TypeA"] = "FAMKEY|Single|",
        });

        var type = Assert.Single(parsed);
        Assert.Equal("TypeA", type.TypeName);
        Assert.Equal("Single", type.FamilyKey);
        Assert.Equal(canonical, RoutingFingerprint.CanonicalSubstring(type.Routing));
    }

    [Fact]
    public void WithCanonicalTransitionGroups_Null_ReturnsNull()
    {
        Assert.Null(RoutingFingerprint.WithCanonicalTransitionGroups(null));
    }

    [Fact]
    public void WithCanonicalTransitionGroups_ShapeGroupRule_MatchesPlainTransitionsSnapshot()
    {
        // Owner stress test #3: the project holds the multi-shape transition
        // in its shape-specific group (7) AFTER Unions (5) in enum order,
        // while the catalog stored the same rule in plain Transitions (4)
        // BEFORE Unions. Without a re-sort after the 7→4 mapping the
        // serialized order diverges and the drift can never clear.
        var live = RoutingFingerprint.WithCanonicalTransitionGroups(new RoutingPreferencesSnapshot(0,
        [
            new RoutingRuleSnapshot(1, "Отвод: 90", "отвод", []),
            new RoutingRuleSnapshot(5, "Муфта: 50", "соединение", []),
            new RoutingRuleSnapshot(7, "Переход: прям>круг", "переход", []),
        ]));
        var catalog = RoutingFingerprint.WithCanonicalTransitionGroups(new RoutingPreferencesSnapshot(0,
        [
            new RoutingRuleSnapshot(1, "Отвод: 90", "отвод", []),
            new RoutingRuleSnapshot(4, "Переход: прям>круг", "переход", []),
            new RoutingRuleSnapshot(5, "Муфта: 50", "соединение", []),
        ]));

        Assert.Equal(RoutingFingerprint.Compute(catalog), RoutingFingerprint.Compute(live));
    }

    [Fact]
    public void WithCanonicalTransitionGroups_AllShapeTokens_MapToFour()
    {
        var normalized = RoutingFingerprint.WithCanonicalTransitionGroups(new RoutingPreferencesSnapshot(0,
        [
            new RoutingRuleSnapshot(7, "A", "a", []),
            new RoutingRuleSnapshot(8, "B", "b", []),
            new RoutingRuleSnapshot(9, "C", "c", []),
        ]));

        Assert.NotNull(normalized);
        Assert.All(normalized!.Rules, r => Assert.Equal(4, r.GroupType));
    }

    [Fact]
    public void WithCanonicalTransitionGroups_WithinGroupOrderAndParamPlacement_Preserved()
    {
        // Rule order WITHIN a group is routing content — the re-sort must be
        // stable. Param groups keep their canonical trailing placement.
        var normalized = RoutingFingerprint.WithCanonicalTransitionGroups(new RoutingPreferencesSnapshot(0,
        [
            new RoutingRuleSnapshot(7, "First", "a", []),
            new RoutingRuleSnapshot(4, "Second", "b", []),
            new RoutingRuleSnapshot(RoutingGroupKeys.ParamGroupType, "P", "p",
                [], GroupKey: RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM")),
            new RoutingRuleSnapshot(7, "Third", "c", []),
        ]));

        Assert.NotNull(normalized);
        Assert.Equal(
            new[] { "First", "Second", "Third", "P" },
            normalized!.Rules.Select(r => r.PartName).ToArray());
    }

    private static RoutingPreferencesSnapshot MakeRouting() => new(1,
    [
        new RoutingRuleSnapshot(0, "Seg-1", "seg", []),
        new RoutingRuleSnapshot(1, "A:С", "a", []),
        new RoutingRuleSnapshot(1, "B:Т", "b", []),
    ]);
}
