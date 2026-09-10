namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Snapshot of a MEP curve type's routing preferences (ADR-056,
/// Issue #159): the fitting/segment selection rules of PipeType,
/// DuctType, CableTrayType and ConduitType
/// (<c>MEPCurveType.RoutingPreferenceManager</c>). Not parameters —
/// editing routing rules leaves every type parameter untouched, so
/// without this section the content hash misses real edits.
/// <c>null</c> on the type means "not a MEP curve type" — a
/// deterministic canonical state.
/// </summary>
/// <param name="PreferredJunctionType"><c>PreferredJunctionType</c>
/// enum ordinal (Tee, Tap, …).</param>
/// <param name="Rules">All rules of all groups, in manager order:
/// groups ascend by <c>RoutingPreferenceRuleGroupType</c> ordinal
/// (Segments=0 … Caps=10, Undefined skipped), rules keep their
/// in-group index. Order is content (first matching rule wins) —
/// do not sort.</param>
public sealed record RoutingPreferencesSnapshot(
    int PreferredJunctionType,
    IReadOnlyList<RoutingRuleSnapshot> Rules);

/// <summary>
/// One routing preference rule.
/// </summary>
/// <param name="GroupType"><c>RoutingPreferenceRuleGroupType</c> enum
/// ordinal (Segments, Elbows, Junctions, …) for manager-based groups;
/// <see cref="RoutingGroupKeys.ParamGroupType"/> (<c>-1</c>) for
/// parameter-based routing (FHV19, ADR-072: flex/conduit/cable-tray types
/// have no RoutingPreferenceManager — their fitting selection lives in
/// visible built-in parameters).</param>
/// <param name="PartName">Resolved name of the referenced MEP part:
/// <c>"{Family}:{Type}"</c> for fitting symbols, element name for
/// segments; <c>null</c> when the rule references
/// <c>InvalidElementId</c> ("no part allowed" — real content, distinct
/// from an unresolved reference).</param>
/// <param name="Description">Rule description as entered by the user
/// (may be empty — escaped into the canonical string as-is).</param>
/// <param name="Criteria">Selection criteria of the rule, in rule
/// order.</param>
/// <param name="GroupKey">String group identity for parameter-based
/// groups (<c>"Param:&lt;BuiltInParameter-name&gt;"</c>); <c>null</c> for
/// manager-based groups, whose identity is <paramref name="GroupType"/>.
/// Kept out of the manager path so pipe/duct ROUTING tokens stay
/// byte-identical to pre-FHV19.</param>
public sealed record RoutingRuleSnapshot(
    int GroupType,
    string? PartName,
    string Description,
    IReadOnlyList<RoutingCriterionSnapshot> Criteria,
    string? GroupKey = null);

/// <summary>
/// One routing criterion. Only <c>PrimarySizeCriterion</c> carries
/// values today; unknown future criterion types contribute their class
/// name with zero bounds so a Revit upgrade never breaks the format.
/// </summary>
/// <param name="CriterionType">Criterion class name (e.g.
/// <c>"PrimarySizeCriterion"</c>).</param>
/// <param name="MinimumSize">Minimum size in internal units (feet),
/// 0 when not applicable.</param>
/// <param name="MaximumSize">Maximum size in internal units (feet),
/// 0 when not applicable.</param>
public sealed record RoutingCriterionSnapshot(
    string CriterionType,
    double MinimumSize,
    double MaximumSize);
