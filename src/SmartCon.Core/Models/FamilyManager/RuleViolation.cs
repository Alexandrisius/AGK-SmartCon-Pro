namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One failed rule evaluation on one family type. Values are raw
/// (invariant culture); the UI layer localizes the operator and formats
/// numbers to display units via the carried unit identifiers.
/// </summary>
/// <param name="TypeName">Family type the rule failed on.</param>
/// <param name="AttributeName">Attribute (parameter) name.</param>
/// <param name="Operator">Operator of the failed rule.</param>
/// <param name="ExpectedValue">Target value of the rule (text or invariant
/// number), or <c>null</c> for value-less operators
/// (IsPresent/HasValue/IsEmpty).</param>
/// <param name="ExpectedMin">Lower bound for Between, in internal units.</param>
/// <param name="ExpectedMax">Upper bound for Between, in internal units.</param>
/// <param name="ActualValue">Actual parameter value on the type (text or
/// invariant number); <c>null</c> when the parameter is absent or empty.</param>
/// <param name="UnitTypeId">Display unit identifier for number formatting
/// in the report UI.</param>
public sealed record RuleViolation(
    string TypeName,
    string AttributeName,
    ValidationRuleOperator Operator,
    string? ExpectedValue,
    double? ExpectedMin,
    double? ExpectedMax,
    string? ActualValue,
    string? UnitTypeId);
