namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One row of <c>item_routing_rules</c> (parent item, type, part token) —
/// the raw input for the routing-phantom detector (#133).
/// </summary>
public sealed record RoutingPartReference(
    string CatalogItemId,
    string FamilyKey,
    string TypeName,
    string PartName);

/// <summary>
/// A routing rule that references a fitting family which no longer exists
/// in the catalog (#133): the "Family:Type" token stays in the rule, but
/// the family was deleted (e.g. purged as missing) — the rule is a dead
/// reference. Detected with a pure catalog pass (rules + name resolution,
/// no Revit) and surfaced as a badge on the family tree; the badge opens
/// the properties directly on the Routing tab at the affected type.
/// </summary>
/// <param name="CatalogItemId">Parent family (the rule owner).</param>
/// <param name="FamilyKey">Locale-invariant identity of the routing type.</param>
/// <param name="TypeName">Routing type whose rule holds the dead reference.</param>
/// <param name="MissingPartFamilyName">Family-name part of the dead "Family:Type" token.</param>
public sealed record RoutingPhantomInfo(
    string CatalogItemId,
    string FamilyKey,
    string TypeName,
    string MissingPartFamilyName);
