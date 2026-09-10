namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One system-level issue found inside a family document during the
/// import health check (broken formula, regeneration failure, document
/// warning). Distinct from <see cref="RuleViolation"/>: health issues
/// come from Revit itself, not from category validation rules.
/// </summary>
/// <param name="TypeName">Family type the issue is tied to, or
/// <c>null</c> for family-level issues (e.g. accumulated document
/// warnings).</param>
/// <param name="Severity">Warning issues are informational (the family
/// may still import); Error issues block the import (hard gate).</param>
/// <param name="Description">Revit failure description text.</param>
public sealed record FamilyHealthIssue(
    string? TypeName,
    FamilyHealthIssueSeverity Severity,
    string Description);
