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

    private static RoutingPreferencesSnapshot MakeRouting() => new(1,
    [
        new RoutingRuleSnapshot(0, "Seg-1", "seg", []),
        new RoutingRuleSnapshot(1, "A:С", "a", []),
        new RoutingRuleSnapshot(1, "B:Т", "b", []),
    ]);
}
