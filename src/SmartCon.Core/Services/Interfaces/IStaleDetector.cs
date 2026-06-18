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
    ///   <item><description>Multiple elements — check items in any of the listed categories.</description></item>
    /// </list>
    /// </param>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<StaleCheckResult>> CheckCategoryAsync(
        IReadOnlyList<string>? categoryIds,
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
    /// </summary>
    FamilyStaleSnapshot? GetMergedSnapshot(IReadOnlyList<StaleCheckResult> newResults);

    /// <summary>
    /// Removes the given catalog item IDs from the snapshot. Used after a successful
    /// Update so the next <c>Check</c> re-evaluates them from scratch instead of
    /// showing the stale marker indefinitely.
    /// </summary>
    void MarkUpdated(IReadOnlyCollection<string> catalogItemIds);

    /// <summary>
    /// Clears the session-scoped snapshot. Called by the host after
    /// Edit / DB-switch operations (D-10). Use <see cref="MarkUpdated"/>
    /// for individual items that were just updated — that keeps the rest
    /// of the snapshot intact.
    /// </summary>
    void InvalidateCache();
}
