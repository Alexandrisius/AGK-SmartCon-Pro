using System.Text;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Canonical fingerprint of routing preferences (ADR-072, World B —
/// owner decision 2026-08-29): routing is a link between catalog
/// families, NOT file content. At FHV20 the ROUTING section left the
/// content hash, but sync / stale-check / placement still need a cheap
/// equality probe between the live project type's routing and the
/// catalog-stored links. The canonical string below is byte-identical
/// to the pre-FHV20 ROUTING section substring, so the fingerprint stays
/// comparable with anything serialized before the migration.
/// </summary>
public static class RoutingFingerprint
{
    /// <summary>
    /// SHA-256 (uppercase hex) of the canonical routing string;
    /// <c>null</c> when the type carries no routing at all (non-MEP
    /// categories) — absence is its own state and never equals an
    /// empty-but-present routing.
    /// </summary>
    public static string? Compute(RoutingPreferencesSnapshot? routing)
        => routing is null ? null : FamilyContentHasher.ComputeSha256Hex(CanonicalSubstring(routing));

    /// <summary>
    /// The criterion type name of the min/max size criterion
    /// (<c>nameof</c> would drag the Revit assembly into Core — I-09).
    /// </summary>
    public const string PrimarySizeCriterionType = "PrimarySizeCriterion";

    /// <summary>
    /// Drops size-range criteria from every rule (owner decision
    /// 2026-08-30): only PIPES carry size ranges in routing — duct/flex/
    /// conduit/cable-tray rules are a plain part choice. Legacy DB records
    /// (V34-era imports stored the criterion for ducts too) must compare
    /// equal to the new criteria-free live extraction, so BOTH sides of a
    /// drift probe pass non-pipe snapshots through this normalization.
    /// </summary>
    public static RoutingPreferencesSnapshot? WithoutSizeCriteria(RoutingPreferencesSnapshot? routing)
    {
        if (routing is null) return null;
        var changed = false;
        var rules = new List<RoutingRuleSnapshot>(routing.Rules.Count);
        foreach (var rule in routing.Rules)
        {
            if (rule.Criteria.Count > 0
                && rule.Criteria.Any(c => c.CriterionType == PrimarySizeCriterionType))
            {
                changed = true;
                rules.Add(rule with
                {
                    Criteria = rule.Criteria
                        .Where(c => c.CriterionType != PrimarySizeCriterionType)
                        .ToList(),
                });
            }
            else
            {
                rules.Add(rule);
            }
        }
        return changed ? routing with { Rules = rules } : routing;
    }

    // RoutingPreferenceRuleGroupType ordinals (frozen, revitapidocs):
    // Transitions=4, TransitionsRectangularToRound=7,
    // TransitionsRectangularToOval=8, TransitionsOvalToRound=9.
    private const int TransitionsGroupToken = 4;

    /// <summary>
    /// Canonicalizes the multi-shape duct transition groups (tokens 7/8/9)
    /// to the plain Transitions token (4) for COMPARISON only (owner stress
    /// test 2026-08-30): a project may hold a round-to-rectangular
    /// transition rule in its shape-specific group while the catalog row
    /// sits in Transitions — Revit accepts the fitting in either, so the
    /// drift probe must see them as equal; without this the stale marker
    /// could never clear. Storage and the editor keep the original tokens.
    /// </summary>
    public static RoutingPreferencesSnapshot? WithCanonicalTransitionGroups(RoutingPreferencesSnapshot? routing)
    {
        if (routing is null) return null;
        var changed = false;
        var rules = new List<RoutingRuleSnapshot>(routing.Rules.Count);
        foreach (var rule in routing.Rules)
        {
            if (rule.GroupType is 7 or 8 or 9)
            {
                changed = true;
                rules.Add(rule with { GroupType = TransitionsGroupToken });
            }
            else
            {
                rules.Add(rule);
            }
        }
        return changed ? routing with { Rules = rules } : routing;
    }

    /// <summary>
    /// The canonical serialization of one type's routing: preferred
    /// junction + every rule (group token, part, description, criteria)
    /// in stored order (rule order IS routing content).
    /// </summary>
    public static string CanonicalSubstring(RoutingPreferencesSnapshot? routing)
    {
        var sb = new StringBuilder(64);
        sb.Append("ROUTING|");
        if (routing is not null)
        {
            sb.Append(routing.PreferredJunctionType).Append('|');
            foreach (var rule in routing.Rules)
            {
                // Parameter-based groups (flex/conduit/cable-tray — no
                // RoutingPreferenceManager) carry a string group key;
                // manager-based groups keep the int token byte-for-byte.
                if (rule.GroupType == RoutingGroupKeys.ParamGroupType)
                    sb.Append(FamilyContentHasher.Escape(rule.GroupKey ?? RoutingGroupKeys.ParamPrefix)).Append('|');
                else
                    sb.Append(rule.GroupType).Append('|');
                sb.Append(FamilyContentHasher.Escape(rule.PartName ?? FamilyContentHasher.NullPartMarker)).Append('|');
                sb.Append(FamilyContentHasher.Escape(rule.Description)).Append('|');
                foreach (var criterion in rule.Criteria)
                {
                    sb.Append(FamilyContentHasher.Escape(criterion.CriterionType)).Append('|');
                    sb.Append(criterion.MinimumSize.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                    sb.Append(criterion.MaximumSize.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                }
            }
        }
        else
        {
            sb.Append('-').Append('|');
        }
        return sb.ToString();
    }
}
