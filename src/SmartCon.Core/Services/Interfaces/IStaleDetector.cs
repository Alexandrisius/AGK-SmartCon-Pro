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
    /// Does not modify the session snapshot.
    /// </summary>
    Task<StaleCheckResult> CheckFamilyAsync(
        string catalogItemId,
        string familyName,
        Document doc,
        ElementId familyId,
        CancellationToken ct);

    /// <summary>
    /// Check every family in the given category (recursively into sub-categories).
    /// Updates the session snapshot on success.
    /// </summary>
    /// <param name="categoryId">Category ID to check. <c>null</c> means uncategorized.</param>
    /// <param name="recursive">If true, sub-categories are included.</param>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<StaleCheckResult>> CheckCategoryAsync(
        string? categoryId,
        bool recursive,
        Document doc,
        CancellationToken ct);

    /// <summary>
    /// Returns the current session-scoped snapshot, or <c>null</c> if no check has run
    /// in this session (or the cache was invalidated).
    /// </summary>
    FamilyStaleSnapshot? GetCachedSnapshot();

    /// <summary>
    /// Clears the session-scoped snapshot. Called by the host after
    /// Load/Update/Edit/DB-switch operations (D-10).
    /// </summary>
    void InvalidateCache();
}
