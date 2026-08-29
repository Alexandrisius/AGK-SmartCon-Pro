namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Routing editor input of one system MEPCurve catalog item (ADR-072,
/// Phase 3). <see cref="IsLegacyVersion"/> marks versions without stored
/// canonical sections (pre-V33) — the editor shows them read-only with the
/// actualization hint because hashes cannot be recomputed verifiably.
/// <see cref="HasTypeNameCollisions"/> marks items whose section storage
/// cannot be recomposed verifiably: same-named types of different system
/// families in one item (documented reality — duct has Round/Rect/Oval
/// families, conduit With/WithoutFittings) collide on the name-keyed
/// section keys of V33 — the editor refuses saves for them (byte-exactness
/// guarantee of <c>RebuildSystemSectionsWithRouting</c>).
/// </summary>
public sealed record RoutingEditorData(
    int HostCategoryId,
    bool IsLegacyVersion,
    bool HasTypeNameCollisions,
    IReadOnlyList<RoutingEditorTypeData> Types,
    IReadOnlyList<FamilyRoutingRuleInfo> Rules,
    IReadOnlyList<FamilyRoutingTypeSettings> Settings,
    IReadOnlyList<string> MissingPartFamilies,
    IReadOnlyList<double> SizeNominalsFeet);

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
    string? NewVersionLabel,
    IReadOnlyList<string> ArchivedLockedParts,
    string? ErrorMessage);

/// <summary>One part-picker row: a candidate family with its current-version types.</summary>
public sealed record RoutingPartCandidate(
    string CatalogItemId,
    string FamilyName,
    string? PartTypeLabel,
    IReadOnlyList<string> TypeNames);
