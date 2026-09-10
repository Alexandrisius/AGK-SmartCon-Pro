namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of the import health check for one family document: system
/// errors/warnings collected by switching every type with Regenerate
/// (pyRevit Family Quick Check pattern) plus accumulated document
/// warnings. <see cref="IsHealthy"/> is <c>false</c> when at least one
/// Error-severity issue exists — the batch import dialog blocks such
/// rows (hard gate).
/// </summary>
public sealed record FamilyHealthReport(
    bool IsHealthy,
    IReadOnlyList<FamilyHealthIssue> Issues)
{
    public static readonly FamilyHealthReport Healthy = new(true, []);

    public static FamilyHealthReport FromIssues(IReadOnlyList<FamilyHealthIssue> issues) =>
        new(issues.All(i => i.Severity != FamilyHealthIssueSeverity.Error), issues);
}
