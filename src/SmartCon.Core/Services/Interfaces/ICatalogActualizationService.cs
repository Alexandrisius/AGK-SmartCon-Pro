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
    /// (the user confirmed the purge on the summary screen). Deletes the
    /// affected versions (FK CASCADE cleans types/attributes/nested),
    /// their file records and assets, and catalog items left without any
    /// version; when a deleted version was the active one, the active
    /// pointer is moved and the item hash/name re-synced. Filesystem
    /// errors do NOT block the cleanup — rows are deleted DB-only.
    /// E5 (#213, ADR-067): an item referenced in <c>family_dependencies</c>
    /// by ANY parent version is NOT purged (dependency guard) — it is
    /// skipped, logged and counted in <c>GuardedSkippedItems</c>.
    /// </summary>
    Task<(int DeletedItems, int DeletedVersions, int FailedDirectories, int GuardedSkippedItems)> PurgeMissingAsync(
        IReadOnlyList<HashRecalculationMissingFile> missing,
        CancellationToken ct = default);
}
