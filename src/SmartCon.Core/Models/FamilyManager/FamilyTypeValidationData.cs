namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One family type with its normalized parameter values — the unit the
/// validation engine evaluates rules against. Every rule is checked on
/// EVERY type; a single failing type fails the family.
/// </summary>
public sealed record FamilyTypeValidationData(
    string TypeName,
    IReadOnlyList<ParameterValidationValue> Values);
