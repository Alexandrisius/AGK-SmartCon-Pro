namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Routing editor input of one system MEPCurve catalog item (ADR-072,
/// World B): routing links live at the ITEM level (V37 tables) — editor
/// edits never create versions and never touch the content hash. Legacy
/// items (no item-level rows yet) fall back to the current version's V34
/// rows so the editor still shows the imported state before backfill.
/// </summary>
public sealed record RoutingEditorData(
    int HostCategoryId,
    IReadOnlyList<RoutingEditorTypeData> Types,
    IReadOnlyList<FamilyRoutingRuleInfo> Rules,
    IReadOnlyList<FamilyRoutingTypeSettings> Settings,
    IReadOnlyList<string> MissingPartFamilies,
    IReadOnlyList<double> SizeNominalsFeet,
    IReadOnlyList<SegmentSizeBounds> SegmentBounds,
    /// <summary>Part-type ordinal (<c>family_facts.part_type</c>) per rule-part
    /// family name — the junctions group greys out tee rules when the
    /// preferred junction is a tap and vice versa (Revit routing dialog
    /// behavior, owner review 2026-08-30). <c>null</c> when unknown.</summary>
    IReadOnlyDictionary<string, int>? PartTypesByFamily = null);

/// <summary>
/// The segment's own configured size span (min/max nominal diameter of its
/// size table, feet) — the read-only Segments row shows it exactly like the
/// Revit routing dialog shows the segment's allowed size range.
/// </summary>
public sealed record SegmentSizeBounds(
    string SegmentName,
    double MinNominalFeet,
    double MaxNominalFeet);

/// <summary>One editable type of the item.</summary>
/// <param name="WithFittings">
/// Conduit/cable-tray discrimination from the family key
/// (<see cref="SystemFamilyKeys"/>) — without-fittings classes hide the
/// TEE/CROSS rows (ADR-072 §2.7). <c>true</c> for every other category.
/// </param>
public sealed record RoutingEditorTypeData(
    string TypeName,
    string FamilyKey,
    string FamilyName,
    bool WithFittings);

/// <summary>Edited routing of one type (all its groups, in rule order).</summary>
public sealed record RoutingEditorTypeSave(
    string TypeName,
    string FamilyKey,
    int PreferredJunctionType,
    IReadOnlyList<FamilyRoutingRuleInfo> Rules);

/// <summary>Editor save input: only the types the user actually touched.</summary>
public sealed record RoutingEditorSave(IReadOnlyList<RoutingEditorTypeSave> EditedTypes);

/// <summary>
/// Save outcome. <see cref="ArchivedLockedParts"/> lists the part families
/// removed from routing that stay dependency-locked because ARCHIVED
/// versions of the item still reference them (ADR-067 — the UX hint
/// prevents the "I removed it from routing but the family won't delete"
/// support case).
/// </summary>
public sealed record RoutingSaveResult(
    bool Success,
    IReadOnlyList<string> ArchivedLockedParts,
    string? ErrorMessage);

/// <summary>One part-picker row: a candidate family with its current-version types.</summary>
public sealed record RoutingPartCandidate(
    string CatalogItemId,
    string FamilyName,
    string? PartTypeLabel,
    IReadOnlyList<string> TypeNames);
