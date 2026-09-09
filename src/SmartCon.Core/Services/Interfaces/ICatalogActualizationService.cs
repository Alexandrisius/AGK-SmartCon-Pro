using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// The catalog actualization engine (ADR-054, THE single "update database"
/// service, docs/architecture/database-migrations.md). Unions the
/// detections of all registered <see cref="IDatabaseActualizationTask"/>,
/// opens each pending family file exactly once (snapshot + geometry in the
/// same session via
/// <see cref="IFamilyMigrationExtractor.ExtractLoadableWithGeometryAsync"/>),
/// and applies only the pending tasks. New extraction-time features
/// register a new task class — the engine, dialog, gate, resume and purge
/// come for free.
/// </summary>
public interface ICatalogActualizationService
{
    /// <summary>
    /// Pending records split by tier and openability: processable Critical
    /// drives the banner/read-only gate, processable Optional only the
    /// visibility of the unified "Обновить базу" command, and the
    /// NewerOnly counts drive the non-blocking amber "needs a newer
    /// Revit" indicator. A task whose check throws contributes 0 (Warn is
    /// logged).
    /// </summary>
    Task<DatabasePendingBreakdown> CountPendingBreakdownAsync(
        int revitMajorVersion, CancellationToken ct = default);

    /// <summary>
    /// Runs the file-free pass of every task, then processes every pending
    /// family group: one open, pending tasks applied in
    /// <see cref="IDatabaseActualizationTask.Order"/>. Cancellation
    /// between groups keeps committed work and returns with
    /// <see cref="DatabaseMigrationResult.WasCancelled"/> = true.
    /// </summary>
    Task<DatabaseMigrationResult> RunAllPendingAsync(
        int revitMajorVersion,
        IProgress<DatabaseMigrationProgress>? progress,
        CancellationToken ct = default);

    /// <summary>
    /// Permanently delete catalog rows whose managed files are missing
    /// (the user confirmed the purge). Deletes the affected versions (FK
    /// CASCADE cleans types/attributes/nested), their file records and
    /// assets, and catalog items left without any version; when a deleted
    /// version was the active one, the active pointer is moved and the item
    /// hash/name re-synced. Filesystem errors do NOT block the cleanup —
    /// rows are deleted DB-only. Dependency links referencing a purged item
    /// are RESET at the parents (family_dependencies FK CASCADE) instead of
    /// blocking the cleanup (#133: a missing fitting can never be loaded
    /// into routing — the link is dead weight); the result reports the count
    /// so the UI can warn the user about stale project routing.
    /// </summary>
    Task<PurgeMissingResult> PurgeMissingAsync(
        IReadOnlyList<HashRecalculationMissingFile> missing,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the catalog versions marked
    /// <see cref="FamilyContentHashFormat.RecalculationMissing"/> (-2) —
    /// the always-cheap part of the candidate list for the cleanup dialog
    /// (Issue #133). One row per (item, version label), with the file path
    /// of the highest stored Revit variant.
    /// </summary>
    Task<IReadOnlyList<MissingRecordCandidate>> LoadMissingRecordCandidatesAsync(CancellationToken ct = default);

    /// <summary>
    /// On-disk scan (Issue #133): checks every managed file of the ACTIVE
    /// catalog (loadable + system) with File.Exists — the primary detector
    /// for files deleted manually while Revit is running (no migration
    /// needed, nothing to close). A version becomes a candidate when EVERY
    /// variant file of its label is absent — a single surviving variant
    /// keeps the record working in its Revit version. Versions already
    /// marked -2 are reported as candidates but not double-counted.
    /// Candidates arrive INCREMENTALLY via <c>progress.Found</c> so an
    /// interrupted scan keeps its partial results; the return value is the
    /// total number of discovered candidates.
    /// </summary>
    Task<int> ScanForMissingFilesAsync(
        IProgress<MissingRecordScanProgress>? progress,
        CancellationToken ct = default);
}
