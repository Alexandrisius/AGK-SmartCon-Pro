using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Catalog compliance check (#259, «Проверить → Правила»): re-validates
/// PERSISTED catalog items against the CURRENT effective validation rules
/// of their category. Pure SQLite + pure <see cref="IFamilyValidationEngine"/>
/// — no Revit API, no open document required, works offline and covers items
/// that are not loaded in any project (unlike the stale check).
/// <para>
/// Semantics are deliberately separate from <see cref="IStaleDetector"/>:
/// staleness = project vs catalog (fixed by «Обновить»), compliance =
/// catalog vs rules (fixed by editing the family and importing a new
/// version — the import gate revalidates on entry).
/// </para>
/// </summary>
public interface ICatalogComplianceService
{
    /// <summary>
    /// Checks every catalog item of the given categories (the caller expands
    /// the subtree; the synthetic id <c>"__no_category__"</c> selects items
    /// without a category, mirroring <c>IStaleDetector.CheckCategoryAsync</c>).
    /// Effective rules are resolved ONCE per category for the whole run.
    /// Results are merged into the session snapshot.
    /// </summary>
    Task<IReadOnlyList<ComplianceCheckResult>> CheckCategoriesAsync(
        IReadOnlyList<string> categoryIds,
        IProgress<ComplianceCheckProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Checks a single catalog item against the rules of ITS category.
    /// A missing item yields <see cref="ComplianceStatus.CannotVerify"/>.
    /// The result is merged into the session snapshot.
    /// </summary>
    Task<ComplianceCheckResult> CheckItemAsync(string catalogItemId, CancellationToken ct = default);

    /// <summary>Current session snapshot, or <c>null</c> when nothing was
    /// checked yet (or the cache was invalidated).</summary>
    CatalogComplianceSnapshot? GetCachedSnapshot();

    /// <summary>Snapshot folded with <paramref name="newResults"/> (same
    /// merge-without-mutation contract as <c>IStaleDetector.GetMergedSnapshot</c>)
    /// — a cold cache starts from the empty snapshot, never a silent no-op.</summary>
    CatalogComplianceSnapshot GetMergedSnapshot(IReadOnlyList<ComplianceCheckResult> newResults);

    /// <summary>Drops the whole snapshot: rule edits (metadata changed),
    /// database switch, database actualization.</summary>
    void InvalidateCache();

    /// <summary>Drops the verdicts of specific items (re-import created a
    /// new version that already passed the import gate).</summary>
    void InvalidateItems(IReadOnlyCollection<string> catalogItemIds);
}
