namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of a catalog compliance check (#259) for ONE catalog item:
/// the item against the effective validation rules of its category.
/// Pure-DB verdict (no Revit, no open document required).
/// </summary>
/// <param name="CatalogItemId">Catalog item the verdict belongs to.</param>
/// <param name="CategoryId">Category the verdict was computed for. Stored so
/// a later category change (DnD / picker) self-invalidates the verdict at
/// apply time: a mismatch between the snapshot entry and the leaf's current
/// category means "checked under other rules" — the badge must not survive.</param>
/// <param name="Status">Pass / Fail / CannotVerify (never NotChecked).</param>
/// <param name="Violations">Failed (type × rule) evaluations — empty unless
/// <see cref="Status"/> is <see cref="ComplianceStatus.Fail"/>.</param>
/// <param name="RulesEvaluated">Total (type × enabled rule) evaluations
/// performed (carried into the validation report dialog).</param>
/// <param name="RuleCount">Number of effective rules configured for the
/// category — the report dialog's "rules checked" count.</param>
public sealed record ComplianceCheckResult(
    string CatalogItemId,
    string? CategoryId,
    ComplianceStatus Status,
    IReadOnlyList<RuleViolation> Violations,
    int RulesEvaluated,
    int RuleCount)
{
    public static ComplianceCheckResult Pass(string catalogItemId, string? categoryId, int ruleCount) =>
        new(catalogItemId, categoryId, ComplianceStatus.Pass, [], 0, ruleCount);

    public static ComplianceCheckResult Fail(
        string catalogItemId,
        string? categoryId,
        IReadOnlyList<RuleViolation> violations,
        int rulesEvaluated,
        int ruleCount) =>
        new(catalogItemId, categoryId, ComplianceStatus.Fail, violations, rulesEvaluated, ruleCount);

    public static ComplianceCheckResult CannotVerify(string catalogItemId, string? categoryId) =>
        new(catalogItemId, categoryId, ComplianceStatus.CannotVerify, [], 0, 0);
}
