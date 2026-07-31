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
public sealed record SystemTypeSnapshot(
    string Name,
    IReadOnlyList<SystemParameterValue> Values,
    CompoundStructureSnapshot? Structure = null,
    RoutingPreferencesSnapshot? Routing = null,
    string? FamilyName = null);

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
