using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// On-demand stale detection for families in the active project
/// (ADR-030, Issue #69). All checks read from
/// <c>IFamilyVersionStore</c> (in-memory ES, no <c>.rfa</c> file I/O).
/// </summary>
public interface IStaleDetector
{
    /// <summary>
    /// Check a single family against the catalog. Returns a <see cref="StaleCheckResult"/>.
    /// Updates the session snapshot for this family (overwrite, not merge).
    /// </summary>
    Task<StaleCheckResult> CheckFamilyAsync(
        string catalogItemId,
        string familyName,
        Document doc,
        ElementId familyId,
        CancellationToken ct);

    /// <summary>
    /// Check every family in the given categories. Merges results into the session snapshot
    /// (existing entries for other categories are preserved; entries for checked families
    /// are overwritten with the fresh result).
    /// </summary>
    /// <param name="categoryIds">
    /// Flat list of category IDs to check. The caller is responsible for expanding the
    /// category tree (recursive=true) before calling — <see cref="IStaleDetector"/> does
    /// not know about the category hierarchy.
    /// <list type="bullet">
    ///   <item><description><c>null</c> — check items in any category (root "Check all").</description></item>
    ///   <item><description>Single element <c>"__no_category__"</c> — check uncategorized items.</description></item>
    ///   <item><description>Single element with a real category ID — check that category only.</description></item>
    ///   <item><description>Multiple elements — check items in any of the listed categories
    ///     (including <c>"__no_category__"</c>, in which case uncategorized items are
    ///     included in addition to the real categories).</description></item>
    /// </list>
    /// </param>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="ct">Cancellation token. Honoured between every <c>await</c>
    /// boundary AND between major steps (catalog read, Revit collector, ES read).
    /// Cancellation throws <see cref="OperationCanceledException"/>.</param>
    /// <remarks>
    /// If two families in the project share a <c>Name</c> (rare — duplicate
    /// loadable variants), the first match is used and a <c>Warn</c> is written
    /// to <c>smartcon.log</c>. The version marker is then written to the
    /// first-match <see cref="ElementId"/>, which may not be the one the
    /// user intended. The duplicate must be resolved by the operator before
    /// stale-detection results are trustworthy.
    /// </remarks>
    Task<IReadOnlyList<StaleCheckResult>> CheckCategoryAsync(
        IReadOnlyList<string>? categoryIds,
        Document doc,
        CancellationToken ct);

    /// <summary>
    /// Check a single system family (mini-project catalog item) against the
    /// catalog (Issue #104). A system item owns N types; the check locates
    /// the item's types in the project by (type name, category), reads their
    /// ES markers and aggregates one verdict: the item is stale when ANY of
    /// its project-loaded types has no marker or a mismatched marker.
    /// Returns <c>null</c> when none of the item's types exist in the project
    /// ("not loaded" — same UX as an unloaded loadable family).
    /// Updates the session snapshot for this item.
    /// </summary>
    Task<StaleCheckResult?> CheckSystemFamilyAsync(
        string catalogItemId,
        string displayName,
        Document doc,
        CancellationToken ct);

    /// <summary>
    /// Returns the current session-scoped snapshot, or <c>null</c> if no check has run
    /// in this session (or the cache was invalidated).
    /// </summary>
    FamilyStaleSnapshot? GetCachedSnapshot();

    /// <summary>
    /// Returns a snapshot that is the <em>logical merge</em> of the current cached
    /// snapshot with the supplied <paramref name="newResults"/>: existing entries
    /// are preserved, entries for the same catalog item ID are overwritten by the
    /// fresh result. Used by the VM to update the tree with the COMPLETE picture
    /// of stale markers (not just the ones from the latest Check call).
    /// <para>
    /// A cold/invalidated cache is NOT an error (#220): the merge then starts
    /// from the empty snapshot, so the apply path always recomputes badges —
    /// the post-DnD tree rebuild must never silently skip them.
    /// </para>
    /// </summary>
    FamilyStaleSnapshot GetMergedSnapshot(IReadOnlyList<StaleCheckResult> newResults);

    /// <summary>
    /// Removes the given catalog item IDs from the snapshot. Used after a successful
    /// Update so the next <c>Check</c> re-evaluates them from scratch instead of
    /// showing the stale marker indefinitely.
    /// </summary>
    /// <param name="catalogItemIds">Catalog item IDs to drop. <c>null</c> or empty
    /// collection is a no-op. The same collection is also used by
    /// <see cref="IStaleCategoryAggregator.FilterStaleBySubtree"/> so the two
    /// are consistent.</param>
    void MarkUpdated(IReadOnlyCollection<string> catalogItemIds);

    /// <summary>
    /// Clears the session-scoped snapshot. Called by the host after
    /// Edit / DB-switch operations (D-10). Use <see cref="MarkUpdated"/>
    /// for individual items that were just updated — that keeps the rest
    /// of the snapshot intact.
    /// </summary>
    void InvalidateCache();

    /// <summary>
    /// Drops ONLY the given catalog item IDs from the snapshot (and their
    /// per-type verdicts) without touching the rest. Called by the batch
    /// import executor after writing version markers: the check results
    /// of the just-imported items are outdated and will be re-evaluated by
    /// the post-import check, but every other family's stale marker must
    /// survive (a full <see cref="InvalidateCache"/> here made previously
    /// flagged families lose their stale badge on the next import).
    /// </summary>
    void InvalidateItems(IReadOnlyCollection<string> catalogItemIds);

    /// <summary>
    /// #187: per-type stale verdicts of one system catalog item
    /// (typeKey "FAMILY|NAME" upper-invariant → isStale), or null when the
    /// item was never checked. Feeds the orange presence dot on the exact
    /// outdated type node in the catalog tree.
    /// </summary>
    IReadOnlyDictionary<string, bool>? GetSystemTypeStaleMap(string catalogItemId);

    /// <summary>
    /// #187: clears ONE type's stale verdict after its successful sync
    /// (per-type "Обновить") — the type's ES marker was just rewritten to the
    /// current catalog version, so its orange dot must clear immediately
    /// without a full "Проверить".
    /// </summary>
    void MarkSystemTypeUpdated(string catalogItemId, string typeKey);
}
