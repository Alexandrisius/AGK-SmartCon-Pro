namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Normalized parameter value consumed by <c>IFamilyValidationEngine</c>.
/// Mapped from <see cref="FamilyParameterValue"/>/<see cref="SystemParameterValue"/>
/// (import-time snapshot) or from <see cref="ExtractedAttributeValue"/>
/// (post-import catalog data) — the engine never sees the source models.
/// </summary>
/// <param name="ParameterName">Parameter name as written in the family.</param>
/// <param name="IsPresent"><c>true</c> if the parameter exists on the family
/// type (regardless of whether it has a value). <c>false</c> maps
/// MissingParameter / NotInFamily extraction statuses.</param>
/// <param name="ValueText">Text value (or resolved element name for
/// ElementId storage) when the parameter has a value; otherwise
/// <c>null</c>. Whitespace-only strings are treated as empty by the
/// engine. SOURCE-DEPENDENT: catalog rows may carry a display string
/// ("300 мм") instead of the canonical invariant text (import-time
/// snapshot path) — string operators (Equals/Contains) are intended for
/// genuine text parameters; numeric parameters must be validated with
/// the numeric operators via <see cref="ValueNumber"/>.</param>
/// <param name="ValueNumber">Numeric value in Revit internal units when
/// applicable; otherwise <c>null</c>.</param>
/// <param name="UnitTypeId">Display unit identifier of the parameter
/// (display metadata for the validation report, not used in rule
/// evaluation).</param>
/// <param name="DisplayNumber">Numeric value in the parameter's DISPLAY
/// units, parsed from the display string ("300 мм" → 300) by
/// <c>DisplayValueParser</c>, or <c>null</c> when not parseable
/// (imperial formats). Numeric rule operators compare
/// <c>DisplayNumber ?? ValueNumber</c> — display units first, because
/// rules are authored in display units.</param>
public sealed record ParameterValidationValue(
    string ParameterName,
    bool IsPresent,
    string? ValueText,
    double? ValueNumber,
    string? UnitTypeId,
    double? DisplayNumber = null);
