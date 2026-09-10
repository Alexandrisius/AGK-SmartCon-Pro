namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One planned parameter-overwrite operation for one family type, derived
/// from the catalog's <see cref="ExtractedAttributeValue"/> rows of the
/// update target version. Pure data — the execution lives in the Revit
/// layer (post-pass of the preserve-types family reload, Issue #239).
/// </summary>
/// <param name="TypeName">Family type name (matches <c>FamilySymbol.Name</c>).</param>
/// <param name="ParameterName">Parameter name (matches <c>FamilySymbol.LookupParameter</c>).</param>
/// <param name="Kind">How to apply the value.</param>
/// <param name="ValueNumber">Numeric value (Revit internal units) for SetDouble/SetInteger.</param>
/// <param name="ValueText">String value for SetString; resolved element name for ResolveElementByName.</param>
public sealed record TypeParameterOverwriteOperation(
    string TypeName,
    string ParameterName,
    TypeParameterOverwriteKind Kind,
    double? ValueNumber,
    string? ValueText);
