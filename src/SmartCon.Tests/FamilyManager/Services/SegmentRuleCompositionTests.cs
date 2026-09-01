using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// <see cref="SegmentRuleComposition"/> (FHV21, owner decision 2026-09-01):
/// the effective routing rule set = fittings from the stored rows +
/// segments from the per-version store (legacy fallback to stored Segments
/// rows); <see cref="SegmentRuleComposition.FromSnapshot"/> is the single
/// per-version records builder for the import writer and both backfills.
/// </summary>
public sealed class SegmentRuleCompositionTests
{
    private static FamilyRoutingRuleInfo Fitting(string typeName, string groupKey, string? part)
        => new(typeName, "Single", groupKey, 0, part, "", []);

    private static SegmentRuleRecord Segment(
        string typeName, string segment, double? min = null, double? max = null, int order = 0)
        => new(typeName, "Single", order, segment, min, max, "rule");

    [Fact]
    public void Compose_PerVersionRowsWin_OverStoredSegments()
    {
        var stored = new[]
        {
            Fitting("T", "Segments", "StoredSeg"),
            Fitting("T", "Elbows", "Elbow:DN50"),
        };
        var perVersion = new[] { Segment("T", "VersionSeg") };

        var rules = SegmentRuleComposition.Compose(stored, perVersion);

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.GroupKey == "Elbows" && r.PartName == "Elbow:DN50");
        var seg = Assert.Single(rules, r => r.GroupKey == "Segments");
        Assert.Equal("VersionSeg", seg.PartName);
    }

    [Fact]
    public void Compose_EmptyPerVersion_FallsBackToStoredSegments()
    {
        // Legacy (pre-FHV21 / backfill pending): the stored Segments rows
        // stay the source until the per-version store is filled.
        var stored = new[]
        {
            Fitting("T", "Segments", "StoredSeg"),
            Fitting("T", "Elbows", "Elbow:DN50"),
        };

        var rules = SegmentRuleComposition.Compose(stored, []);

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.GroupKey == "Segments" && r.PartName == "StoredSeg");
    }

    [Fact]
    public void ToRuleInfo_CriterionAttachedOnlyWhenBoundSet()
    {
        var unrestricted = SegmentRuleComposition.ToRuleInfo(Segment("T", "Seg A"));
        Assert.Empty(unrestricted.Criteria);

        var ranged = SegmentRuleComposition.ToRuleInfo(Segment("T", "Seg A", 0.05, 0.15));
        var criterion = Assert.Single(ranged.Criteria);
        Assert.Equal("PrimarySizeCriterion", criterion.CriterionType);
        Assert.Equal(0.05, criterion.MinimumSize);
        Assert.Equal(0.15, criterion.MaximumSize);

        // One-sided bounds complete with the unrestricted sentinel.
        var maxOnly = SegmentRuleComposition.ToRuleInfo(Segment("T", "Seg A", null, 0.15));
        Assert.Equal(0.0, Assert.Single(maxOnly.Criteria).MinimumSize);
        Assert.Equal(0.15, Assert.Single(maxOnly.Criteria).MaximumSize);
    }

    [Fact]
    public void FromSnapshot_CollectsSegmentRulesInOrderWithCriteria()
    {
        var snapshot = new SystemFamilySnapshot("Pipes", -2008044,
        [
            new SystemTypeSnapshot("T", [], Routing: new RoutingPreferencesSnapshot(0,
            [
                new RoutingRuleSnapshot(0, "Steel", "main",
                    [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.05, 0.15)]),
                new RoutingRuleSnapshot(0, "Copper", "wide", []),
                new RoutingRuleSnapshot(1, "Elbow:DN50", "elbow", []), // fittings excluded
            ]), FamilyKey: "Pipe.Types"),
        ]);

        var records = SegmentRuleComposition.FromSnapshot(snapshot);

        Assert.Equal(2, records.Count);
        Assert.Equal("Steel", records[0].SegmentName);
        Assert.Equal(0, records[0].RuleOrder);
        Assert.Equal(0.05, records[0].MinSizeFeet);
        Assert.Equal(0.15, records[0].MaxSizeFeet);
        Assert.Equal("Pipe.Types", records[0].FamilyKey);
        Assert.Equal("Copper", records[1].SegmentName);
        Assert.Equal(1, records[1].RuleOrder);
        Assert.Null(records[1].MinSizeFeet);
        Assert.Null(records[1].MaxSizeFeet);
    }

    [Fact]
    public void FromSnapshot_RoundTripsThroughToRuleInfo()
    {
        var snapshot = new SystemFamilySnapshot("Pipes", -2008044,
        [
            new SystemTypeSnapshot("T", [], Routing: new RoutingPreferencesSnapshot(0,
            [
                new RoutingRuleSnapshot(0, "Steel", "main",
                    [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.05, 0.15)]),
            ]), FamilyKey: "Pipe.Types"),
        ]);

        var record = Assert.Single(SegmentRuleComposition.FromSnapshot(snapshot));
        var row = SegmentRuleComposition.ToRuleInfo(record);

        Assert.Equal("Segments", row.GroupKey);
        Assert.Equal("Steel", row.PartName);
        Assert.Equal("main", row.Description);
        var criterion = Assert.Single(row.Criteria);
        Assert.Equal(0.05, criterion.MinimumSize);
        Assert.Equal(0.15, criterion.MaximumSize);
    }
}
