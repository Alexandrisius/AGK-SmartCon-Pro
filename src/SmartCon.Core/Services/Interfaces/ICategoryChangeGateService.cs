namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Import Validation Gate for CATEGORY CHANGE inside the catalog (DnD in
/// the tree, category picker in family properties). Same hard-gate
/// semantics as the import: a family may enter a category with rules
/// only by passing them. The check runs against the persisted extracted
/// values of the item's active version — no .rfa re-open.
/// </summary>
public interface ICategoryChangeGateService
{
    /// <summary>
    /// Verifies that the family passes the target category's validation
    /// rules. Returns <c>true</c> when the change is allowed (no rules,
    /// rules passed, or target is "no category"). Returns <c>false</c>
    /// when the change is BLOCKED — in that case the service has already
    /// shown the user the appropriate dialog (violations report or the
    /// "no extraction data" notice), so the caller simply aborts.
    /// </summary>
    Task<bool> EnsureFamilyPassesAsync(
        string catalogItemId,
        string familyName,
        string? targetCategoryId,
        string targetCategoryPath,
        CancellationToken ct = default);
}
