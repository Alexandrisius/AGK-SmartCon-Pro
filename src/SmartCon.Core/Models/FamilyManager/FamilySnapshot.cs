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
/// sorted by type name. Unnamed types are skipped.</param>
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
public sealed record FamilyTypeSnapshot(
    string Name,
    IReadOnlyList<FamilyParameterValue> Values);

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
public sealed record FamilyParameterValue(
    string ParameterName,
    string StorageType,
    bool HasValue,
    string? ValueText,
    double? ValueNumber,
    string? ResolvedElementName);
