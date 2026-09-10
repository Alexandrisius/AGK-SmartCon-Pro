using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// FHV21 (owner decision 2026-09-01): composes the EFFECTIVE routing rule
/// set of a system item — fitting rules from the stored routing rows
/// (item-level World B links) plus segment rules from the per-version
/// store (versioned mini-project content). Every routing reader
/// (routing tab, sync, drift probes) uses this single composition so the
/// segments group always follows the active version while fittings stay
/// item-level. Legacy fallback: when the version has no per-version rows
/// (pre-FHV21 import, backfill pending), the stored Segments rows of the
/// routing tables are used verbatim.
/// </summary>
public static class SegmentRuleComposition
{
    /// <summary>Storage group key of the Segments manager group.</summary>
    public static string SegmentsGroupKey { get; } =
        RoutingGroupKeys.ForManagerGroup((int)RoutingManagerGroup.Segments);

    /// <summary>Max sentinel of an unrestricted criterion (mirrors the
    /// editor's «Все» — 10000 ft covers every real diameter).</summary>
    private const double UnrestrictedMaxFeet = 10000.0;

    /// <summary>The Segments group is per-version content — it never
    /// belongs to the item-level routing channel.</summary>
    public static bool IsSegmentsGroup(string groupKey)
        => string.Equals(groupKey, SegmentsGroupKey, StringComparison.Ordinal);

    /// <summary>
    /// Fittings from <paramref name="storedRules"/> + segments from
    /// <paramref name="perVersionSegmentRules"/> (or the stored Segments
    /// rows when the per-version store is empty for this version).
    /// </summary>
    public static IReadOnlyList<FamilyRoutingRuleInfo> Compose(
        IReadOnlyList<FamilyRoutingRuleInfo> storedRules,
        IReadOnlyList<SegmentRuleRecord> perVersionSegmentRules)
    {
        var fittings = storedRules.Where(r => !IsSegmentsGroup(r.GroupKey));
        var segments = perVersionSegmentRules.Count > 0
            ? perVersionSegmentRules.Select(ToRuleInfo)
            : storedRules.Where(r => IsSegmentsGroup(r.GroupKey));
        return fittings.Concat(segments).ToList();
    }

    /// <summary>Per-version record → routing-rule row of the Segments
    /// group (criterion attached only when at least one bound is set).</summary>
    public static FamilyRoutingRuleInfo ToRuleInfo(SegmentRuleRecord r)
    {
        var criteria = new List<RoutingCriterionSnapshot>();
        if (r.MinSizeFeet is not null || r.MaxSizeFeet is not null)
        {
            criteria.Add(new RoutingCriterionSnapshot(
                "PrimarySizeCriterion",
                r.MinSizeFeet ?? 0.0,
                r.MaxSizeFeet ?? UnrestrictedMaxFeet));
        }
        return new FamilyRoutingRuleInfo(
            r.TypeName, r.FamilyKey, SegmentsGroupKey, r.RuleOrder,
            r.SegmentName, r.Description, criteria);
    }

    /// <summary>
    /// Per-version segment records from a system snapshot (the mini's own
    /// Segments routing rules, in rule order per type) — the single source
    /// for the import writer and both backfill tasks. Routing-rule rows of
    /// the Segments group → records (inverse of <see cref="ToRuleInfo"/>).
    /// </summary>
    public static List<SegmentRuleRecord> FromSnapshot(SystemFamilySnapshot snapshot)
    {
        var records = new List<SegmentRuleRecord>();
        foreach (var type in snapshot.Types)
        {
            var segmentRules = type.Routing?.Rules
                .Where(r => r.GroupType == (int)RoutingManagerGroup.Segments
                    && r.PartName is not null)
                .ToList();
            if (segmentRules is null or { Count: 0 })
                continue;
            var order = 0;
            foreach (var rule in segmentRules)
            {
                var primary = rule.Criteria
                    .FirstOrDefault(c => c.CriterionType == "PrimarySizeCriterion");
                records.Add(new SegmentRuleRecord(
                    type.Name,
                    type.FamilyKey ?? string.Empty,
                    order++,
                    rule.PartName!,
                    primary?.MinimumSize,
                    primary?.MaximumSize,
                    rule.Description));
            }
        }
        return records;
    }
}
