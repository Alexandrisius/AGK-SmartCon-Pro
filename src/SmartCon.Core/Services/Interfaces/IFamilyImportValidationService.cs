using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Orchestrates the import validation gate: resolves a category's
/// effective validation rules (direct + inherited bindings) and runs the
/// pure <see cref="IFamilyValidationEngine"/> over the family's data —
/// from the in-memory Prepare snapshot (import path) or from persisted
/// extracted values (category-change path). No Revit API.
/// </summary>
public interface IFamilyImportValidationService
{
    /// <summary>
    /// Resolves all enabled validation rules effective for
    /// <paramref name="categoryId"/> (own bindings + inherited from
    /// ancestors). Empty list for <c>null</c> category or a category
    /// without rules — such families pass the gate freely.
    /// </summary>
    Task<IReadOnlyList<EffectiveValidationRule>> GetEffectiveRulesAsync(string? categoryId, CancellationToken ct = default);

    /// <summary>
    /// Runs the engine over the in-memory snapshots carried by the batch
    /// row (no .rfa re-open). Exactly one snapshot is non-null.
    /// </summary>
    FamilyValidationReport ValidateFromSnapshots(
        FamilySnapshot? loadableSnapshot,
        SystemFamilySnapshot? systemSnapshot,
        IReadOnlyList<EffectiveValidationRule> rules);

    /// <summary>
    /// Runs the engine over the persisted extracted values of the
    /// catalog item's ACTIVE version (category-change gate). Returns
    /// <c>null</c> when the item has no extraction data — the caller
    /// must surface this as "cannot verify" (hard gate blocks).
    /// </summary>
    Task<FamilyValidationReport?> ValidateCatalogItemAsync(
        string catalogItemId,
        IReadOnlyList<EffectiveValidationRule> rules,
        CancellationToken ct = default);
}
