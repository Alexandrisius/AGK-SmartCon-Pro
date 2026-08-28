namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One stored routing rule of a system MEPCurve type (ADR-072, V34 table
/// <c>family_routing_rules</c>). Rules are version-scoped and per-type:
/// the mini-project carries no fittings, so the routing of the catalog
/// version lives here as data, not in Revit form.
/// </summary>
/// <param name="TypeName">Owning system type name.</param>
/// <param name="FamilyKey">Locale-invariant system family key of the type
/// (one category item can hold same-named types of different families,
/// FHV6).</param>
/// <param name="GroupKey">String group identity — manager group name
/// (<c>"Segments"</c>, <c>"Elbows"</c>, …) or
/// <c>"Param:&lt;BIP&gt;"</c> for parameter-based routing
/// (<see cref="RoutingGroupKeys"/>).</param>
/// <param name="RuleOrder">Zero-based order inside the group (first
/// matching rule wins — order is content).</param>
/// <param name="PartName">Resolved part token (<c>"Family:Type"</c> for
/// fittings, element name for segments); <c>null</c> = no-part rule
/// ("Нет" — legal content).</param>
/// <param name="Description">Rule description (may be empty).</param>
/// <param name="Criteria">Selection criteria in rule order (serialized
/// as JSON in storage — arbitrary criterion kinds survive roundtrip).</param>
public sealed record FamilyRoutingRuleInfo(
    string TypeName,
    string FamilyKey,
    string GroupKey,
    int RuleOrder,
    string? PartName,
    string Description,
    IReadOnlyList<RoutingCriterionSnapshot> Criteria);

/// <summary>
/// Per-type routing scalar of a system MEPCurve type (V34 table
/// <c>family_routing_type_settings</c>).
/// </summary>
/// <param name="TypeName">Owning system type name.</param>
/// <param name="FamilyKey">Locale-invariant system family key.</param>
/// <param name="PreferredJunctionType"><c>PreferredJunctionType</c>
/// ordinal (manager) or <c>RBS_CURVETYPE_PREFERRED_BRANCH_PARAM</c> value
/// (flex).</param>
public sealed record FamilyRoutingTypeSettings(
    string TypeName,
    string FamilyKey,
    int PreferredJunctionType);
