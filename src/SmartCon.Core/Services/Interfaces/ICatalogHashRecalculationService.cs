using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// One-shot, user-initiated data-repair operation that recalculates all
/// stale (format v1 / NULL) content hashes in the catalog database to
/// the rename-invariant format v2 (Issue #126).
/// </summary>
/// <remarks>
/// This is deliberately NOT part of the schema migrator: recalculation
/// requires opening every managed family file in Revit (slow, needs the
/// Revit main thread, cancellable, progress-reporting). The schema
/// migrator stays pure DDL.
/// <para>
/// <c>hash_format_version</c> semantics:
/// <list type="bullet">
/// <item>NULL / 1 — stale, pending recalculation.</item>
/// <item>2 — current rename-invariant format.</item>
/// <item>-1 (<see cref="FamilyContentHashFormat.RecalculationSkipped"/>) —
/// permanently skipped (unreadable file); never retried and excluded
/// from the pending count.</item>
/// </list>
/// </para>
/// <para>
/// System-family rows are migrated by a cheap flag update: their v1
/// canonical string never contained a name, so the hashes are already
/// rename-invariant. Only loadable rows are recalculated from files.
/// </para>
/// <para>
/// Versions whose only file variants are saved in a Revit version NEWER
/// than the running one are left pending — Revit cannot open newer
/// files. They are offered again when the catalog is opened in a newer
/// Revit.
/// </para>
/// </remarks>
public interface ICatalogHashRecalculationService
{
    /// <summary>
    /// Count distinct (catalog_item, version_label) loadable groups that
    /// can be recalculated in the running Revit (at least one file
    /// variant with <c>revit_major_version &lt;= currentRevitMajorVersion</c>)
    /// and are not yet on format v2.
    /// </summary>
    Task<int> CountPendingAsync(int currentRevitMajorVersion, CancellationToken ct = default);

    /// <summary>
    /// Recalculate all pending hashes. System rows are re-flagged first
    /// (instant), then loadable files are opened one at a time
    /// (open → extract → close) and committed to SQLite in batches.
    /// Cancellation commits the current batch and returns with
    /// <see cref="CatalogHashRecalculationResult.WasCancelled"/> = true.
    /// </summary>
    Task<CatalogHashRecalculationResult> RecalculateAsync(
        int currentRevitMajorVersion,
        IProgress<CatalogHashRecalculationProgress>? progress,
        CancellationToken ct = default);

    /// <summary>
    /// Permanently delete catalog rows whose managed files are missing
    /// (the user confirmed the purge on the summary screen). Deletes the
    /// affected versions (FK CASCADE cleans types/attributes/nested),
    /// their file records and assets, and catalog items left without any
    /// version. When a deleted version was the active one, the active
    /// pointer is moved to the newest remaining version and the item's
    /// content hash / name are re-synchronized.
    /// Filesystem errors (network drive permissions) do NOT block the
    /// cleanup — rows are deleted DB-only and the stale directories are
    /// reported for manual removal.
    /// </summary>
    /// <returns>Number of deleted catalog items + versions, and the number
    /// of directories that could not be removed (manual cleanup needed).</returns>
    Task<(int DeletedItems, int DeletedVersions, int FailedDirectories)> PurgeMissingAsync(
        IReadOnlyList<HashRecalculationMissingFile> missing,
        CancellationToken ct = default);
}
