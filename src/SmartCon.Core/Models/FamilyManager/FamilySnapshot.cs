namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Structured snapshot of a loadable family (.rfa) used to compute a
/// <see cref="FamilyContentHash"/>. Extracted in-memory from an open
/// family document — never from file bytes — so it is stable across
/// SaveAs, rename, and Revit upgrade.
/// </summary>
/// <param name="FamilyName">Family name (from <c>FamilyManager</c> or
/// the family document title).</param>
/// <param name="Category">Family category display name (e.g. "Pipe
/// Fittings"). Used in the hash so a category change shifts it.</param>
/// <param name="Parameters">All family parameters (schema level),
/// sorted by name for deterministic output.</param>
/// <param name="Types">All family types with their parameter values,
/// sorted by type name. An unnamed default type is extracted only when
/// the family has no user-created types — under the hash-stable
/// synthetic name <see cref="FamilyTypeSnapshot.DefaultTypeName"/>;
/// otherwise it is skipped as a phantom duplicate of the current
/// type.</param>
/// <param name="Geometry">Aggregated geometry metrics from all
/// <c>GenericForm</c> elements.</param>
/// <param name="SharedNestedFamilyNames">Names of shared nested
/// families referenced by this family (ADR-034). Sorted by name.</param>
public sealed record FamilySnapshot(
    string FamilyName,
    string Category,
    IReadOnlyList<FamilyParameterInfo> Parameters,
    IReadOnlyList<FamilyTypeSnapshot> Types,
    GeometryMetrics Geometry,
    IReadOnlyList<string> SharedNestedFamilyNames);

/// <summary>
/// Schema-level parameter of a loadable family (from
/// <c>FamilyManager.GetParameters()</c>).
/// </summary>
/// <param name="Name">Parameter definition name.</param>
/// <param name="StorageType">Storage type string: <c>"Double"</c>,
/// <c>"String"</c>, <c>"Integer"</c>, <c>"ElementId"</c>, or
/// <c>"None"</c>.</param>
/// <param name="ParameterGroup">Parameter group identifier (TypeId
/// string on R2024+, enum name on R19-R23).</param>
/// <param name="IsInstance"><c>true</c> for instance parameters,
/// <c>false</c> for type parameters.</param>
/// <param name="IsShared"><c>true</c> for shared parameters.</param>
/// <param name="Formula">Formula string, or <c>null</c> if no formula.</param>
/// <param name="IsDeterminedByFormula"><c>true</c> if the value is
/// computed by a formula.</param>
/// <param name="IsReporting"><c>true</c> for reporting parameters.</param>
/// <param name="SharedParamGuid">Shared parameter GUID string, or <c>null</c>.</param>
/// <param name="BuiltInParameterId">For built-in parameters, the
/// <c>BuiltInParameter</c> enum name (e.g. <c>"ALL_MODEL_TYPE_NAME"</c>);
/// <c>null</c> for user-defined/shared parameters.</param>
public sealed record FamilyParameterInfo(
    string Name,
    string StorageType,
    string ParameterGroup,
    bool IsInstance,
    bool IsShared,
    string? Formula,
    bool IsDeterminedByFormula,
    bool IsReporting,
    string? SharedParamGuid,
    string? BuiltInParameterId);

/// <summary>
/// A single family type with all its parameter values.
/// </summary>
/// <param name="Name">Type name (e.g. "DN50").</param>
/// <param name="Values">Parameter values for this type, sorted by
/// parameter name.</param>
/// <param name="UniqueId">Revit UniqueId of the <c>FamilySymbol</c>
/// backing this type (loadable families only). <c>null</c> for system
/// families or when the symbol could not be resolved. Used by the
/// snapshot-to-DB mapper to populate <see cref="FamilyTypeDescriptor.UniqueId"/>
/// without re-opening the .rfa. NOT part of the content hash —
/// <see cref="FamilyContentHasher"/> ignores this field.</param>
public sealed record FamilyTypeSnapshot(
    string Name,
    IReadOnlyList<FamilyParameterValue> Values,
    string? UniqueId = null)
{
    /// <summary>
    /// Synthetic hash-stable name for the unnamed default family type.
    /// Used only when the family has no user-created (named) types at all —
    /// renaming the file must not shift the content hash, and the type name
    /// participates in the canonical string (<see cref="FamilyContentHasher"/>).
    /// UI layers must display the family name instead of this literal.
    /// </summary>
    public const string DefaultTypeName = "<default>";

    /// <summary>
    /// Single display rule for <see cref="DefaultTypeName"/>: the unnamed
    /// default type is always shown to the user under the family name,
    /// never as the raw synthetic literal. Every UI surface (catalog tree,
    /// properties tabs, batch import tooltip) must resolve through this
    /// method instead of comparing against <see cref="DefaultTypeName"/>
    /// inline.
    /// </summary>
    public static string ResolveDisplayName(string typeName, string familyName) =>
        typeName == DefaultTypeName ? familyName : typeName;
}

/// <summary>
/// One parameter value on one family type. Distinguishes "no value"
/// (<see cref="HasValue"/>=<c>false</c>) from "value is zero"
/// (<see cref="HasValue"/>=<c>true</c>, <see cref="ValueNumber"/>=0) so
/// the hash can tell them apart.
/// </summary>
/// <param name="ParameterName">Name of the parameter.</param>
/// <param name="StorageType">Storage type string (see
/// <see cref="FamilyParameterInfo.StorageType"/>).</param>
/// <param name="HasValue"><c>true</c> if the type has a value for this
/// parameter; <c>false</c> if the value is unset/empty.</param>
/// <param name="ValueText">Canonical text representation of the value
/// (invariant culture), or <c>null</c> if <see cref="HasValue"/> is
/// <c>false</c>.</param>
/// <param name="ValueNumber">Numeric value for <c>Double</c>/<c>Integer</c>
/// storage types (invariant culture), or <c>null</c>.</param>
/// <param name="ResolvedElementName">For <c>ElementId</c> storage types,
/// the resolved element name (e.g. material name); <c>null</c> if the
/// id is invalid or the storage type is not <c>ElementId</c>.</param>
/// <param name="ValueDisplay">Human-readable value formatted per the
/// owning document's unit settings with the unit symbol appended
/// (e.g. "300 мм", "16 бар"); <c>null</c> when formatting is not
/// applicable (non-measurable specs, non-Double storage types).
/// NOT part of the content hash — display metadata only.</param>
/// <param name="SpecTypeId">Forge spec identifier of the parameter
/// (e.g. <c>autodesk.spec.aec:length-2.0.0</c>), or the legacy
/// <c>ParameterType</c> enum name on R19-R20; <c>null</c> when unknown.
/// NOT part of the content hash.</param>
/// <param name="UnitTypeId">Display unit identifier of the parameter in
/// the owning document (e.g. <c>autodesk.unit.unit:millimeters-1.0.1</c>),
/// or the legacy <c>DisplayUnitType</c> enum name on R19-R20;
/// <c>null</c> when unknown. NOT part of the content hash.</param>
public sealed record FamilyParameterValue(
    string ParameterName,
    string StorageType,
    bool HasValue,
    string? ValueText,
    double? ValueNumber,
    string? ResolvedElementName,
    string? ValueDisplay = null,
    string? SpecTypeId = null,
    string? UnitTypeId = null);
