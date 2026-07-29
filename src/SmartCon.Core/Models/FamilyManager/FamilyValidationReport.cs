namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of validating one family against a category's effective rules.
/// </summary>
/// <param name="IsValid"><c>true</c> when no violations were found (or no
/// enabled rules exist for the category).</param>
/// <param name="Violations">All failed evaluations, one per
/// (type, rule).</param>
/// <param name="RulesEvaluated">Total (type × enabled rule) evaluations
/// performed — lets the report distinguish "no rules configured" from
/// "rules configured, all passed".</param>
public sealed record FamilyValidationReport(
    bool IsValid,
    IReadOnlyList<RuleViolation> Violations,
    int RulesEvaluated);
