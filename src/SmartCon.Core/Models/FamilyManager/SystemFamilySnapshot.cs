namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Structured snapshot of a system family (category + types) inside a
/// project (.rvt). Used to compute a <see cref="FamilyContentHash"/>
/// for system families. Extracted in-memory from the active project
/// document.
/// </summary>
/// <param name="CategoryName">Display name of the system category
/// (e.g. "Трубы", "Воздуховоды").</param>
/// <param name="CategoryId">Numeric <c>BuiltInCategory</c> ordinal
/// carried as <see cref="int"/> so Core does not depend on
/// <c>Autodesk.Revit.DB</c>.</param>
/// <param name="Types">All selected system types with their parameter
/// values, sorted by type name.</param>
public sealed record SystemFamilySnapshot(
    string CategoryName,
    int CategoryId,
    IReadOnlyList<SystemTypeSnapshot> Types);

/// <summary>
/// A single system-family type (e.g. "Стандартный", "DN50") with all
/// its parameter values.
/// </summary>
/// <param name="Name">Type name.</param>
/// <param name="Values">Parameter values for this type, sorted by
/// parameter name.</param>
/// <param name="Structure">Compound structure (layer stack) for
/// wall/floor/roof/ceiling types, or <c>null</c> when the type has none
/// (ADR-056). Part of the content hash since FHV3.</param>
/// <param name="Routing">Routing preferences for MEP curve types
/// (pipe/duct/cable tray/conduit), or <c>null</c> for other types
/// (ADR-056). Part of the content hash since FHV3.</param>
/// <param name="FamilyName">Revit system family of the type
/// (e.g. "Conduit with Fittings" vs "Conduit without Fittings") — the
/// identity key of Issue #183: sync matches types by
/// (FamilyName, Name, category), never by name alone. SYNC-ONLY field —
/// NOT part of the content hash (FHV4 candidate, Issue #179). Nullable
/// for backward compatibility with pre-#183 snapshot producers.</param>
/// <param name="FamilyKey">Locale-invariant family identity (Issue #190,
/// ADR-064) — one of <see cref="SystemFamilyKeys"/>. Preferred over
/// <paramref name="FamilyName"/> for sync/stale matching when present.
/// Part of the content hash since FHV4 (ADR-065).</param>
/// <param name="Stairs">Stairs subtype references (run/landing/supports/
/// cut mark) by NAME — identity summary for the FHV4 hash (Issue #184,
/// ADR-065). <c>null</c> for non-stairs types. Sync reads the subtype
/// data live from the mini-project (ADR-061), not from this snapshot.</param>
/// <param name="Railing">Railing structure identity summary (top rail,
/// handrails, non-continuous rails, baluster placement) for the FHV4
/// hash (ADR-065). <c>null</c> for non-railing types.</param>
/// <param name="Segments">Segment size tables referenced by the routing
/// rules of a MEP curve type — FHV4 hash content (Issue #179, ADR-065).
/// <c>null</c> when the type has no routing/segments.</param>
/// <param name="Wire">Wire settings identity summary (material,
/// temperature rating, insulation, max size, conduit, neutral scalars)
/// for the FHV5 hash. <c>null</c> for non-wire types. These live on the
/// <c>WireType</c> API properties, NOT in <c>Element.Parameters</c> —
/// the generic parameter pipeline never sees them (manual test
/// 2026-08-04: a wire material change did not sync).</param>
public sealed record SystemTypeSnapshot(
    string Name,
    IReadOnlyList<SystemParameterValue> Values,
    CompoundStructureSnapshot? Structure = null,
    RoutingPreferencesSnapshot? Routing = null,
    string? FamilyName = null,
    string? FamilyKey = null,
    StairsSubtypesSnapshot? Stairs = null,
    RailingStructureSnapshot? Railing = null,
    IReadOnlyList<SegmentSnapshot>? Segments = null,
    WireSettingsSnapshot? Wire = null);

/// <summary>
/// Identity summary of a stairs type's subtype references (Issue #184,
/// ADR-065): the NAMES of the referenced run/landing/support/cut-mark
/// types. Names are user content (not UI-localized), so they survive
/// cross-locale extraction. <c>null</c> = the reference is not set
/// (e.g. no middle supports).
/// </summary>
public sealed record StairsSubtypesSnapshot(
    string? RunTypeName,
    string? LandingTypeName,
    string? LeftSupportTypeName,
    string? RightSupportTypeName,
    string? MiddleSupportTypeName,
    string? CutMarkTypeName);

/// <summary>
/// Identity summary of a railing type's structure (ADR-065): top rail,
/// handrails, the non-continuous rail list and the baluster placement
/// scalars. Element references are carried by NAME (user content,
/// locale-stable).
/// </summary>
public sealed record RailingStructureSnapshot(
    string? TopRailTypeName,
    double? TopRailHeight,
    string? PrimaryHandrailTypeName,
    double? PrimaryHandrailHeight,
    double? PrimaryHandrailLateralOffset,
    int? PrimaryHandrailPosition,
    string? SecondaryHandrailTypeName,
    double? SecondaryHandrailHeight,
    double? SecondaryHandrailLateralOffset,
    int? SecondaryHandrailPosition,
    IReadOnlyList<RailingRailSnapshot> Rails,
    RailingBalusterSnapshot Balusters);

/// <summary>
/// Identity summary of a wire type's settings-graph references: the
/// material / temperature rating / insulation / max size / conduit are
/// NOT parameters — on Revit ≤2025 they are <c>WireType</c> properties
/// pointing at <c>ElectricalSetting</c> objects (WireMaterialType →
/// TemperatureRatingType → InsulationType/WireSize, WireConduitType);
/// on Revit 2026+ the model was replaced by Conductor* elements. Names
/// are user content (locale-stable). <c>null</c> = not set.
/// </summary>
public sealed record WireSettingsSnapshot(
    string? MaterialName,
    string? TemperatureRatingName,
    string? InsulationName,
    string? MaxSizeName,
    string? ConduitName,
    double? NeutralMultiplier,
    bool? NeutralRequired);

/// <summary>One non-continuous rail of a <see cref="RailingStructureSnapshot"/>.</summary>
public sealed record RailingRailSnapshot(
    string Name,
    double Height,
    double Offset,
    string? ProfileName,
    string? MaterialName);

/// <summary>
/// Baluster placement identity summary (ADR-065): main-pattern scalars +
/// the baluster family names in pattern order + per-tread settings.
/// </summary>
public sealed record RailingBalusterSnapshot(
    double PatternLength,
    int DistributionJustification,
    int BreakPattern,
    IReadOnlyList<string?> BalusterFamilyNames,
    bool UseBalusterPerTreadOnStairs,
    int BalusterPerTreadNumber,
    string? BalusterPerTreadFamilyName);

/// <summary>
/// One parameter value on one system-family type. Same semantics as
/// <see cref="FamilyParameterValue"/> — distinguishes "no value" from
/// "zero".
/// </summary>
/// <param name="ParameterName">Name of the parameter.</param>
/// <param name="StorageType">Storage type string.</param>
/// <param name="HasValue"><c>true</c> if the type has a value;
/// <c>false</c> if unset/empty.</param>
/// <param name="ValueText">Canonical text (invariant culture) or
/// <c>null</c>.</param>
/// <param name="ValueNumber">Numeric value or <c>null</c>.</param>
/// <param name="ResolvedElementName">For <c>ElementId</c> values, the
/// resolved element name; <c>null</c> otherwise.</param>
/// <param name="ValueDisplay">Human-readable value formatted per the
/// owning document's unit settings with the unit symbol appended
/// (e.g. "300 мм", "16 бар"); <c>null</c> when not applicable.
/// NOT part of the content hash — display metadata only.</param>
/// <param name="SpecTypeId">Forge spec identifier of the parameter,
/// or the legacy <c>ParameterType</c> enum name on R19-R20;
/// <c>null</c> when unknown. NOT part of the content hash.</param>
/// <param name="UnitTypeId">Display unit identifier of the parameter in
/// the owning document, or the legacy <c>DisplayUnitType</c> enum name
/// on R19-R20; <c>null</c> when unknown. NOT part of the content hash.</param>
public sealed record SystemParameterValue(
    string ParameterName,
    string StorageType,
    bool HasValue,
    string? ValueText,
    double? ValueNumber,
    string? ResolvedElementName,
    string? ValueDisplay = null,
    string? SpecTypeId = null,
    string? UnitTypeId = null);
