namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Shared "database update required" state for the FamilyManager catalog
/// (docs/architecture/database-migrations.md, Issue #126). Singleton: the
/// main VM refreshes the state after init / every database switch; any
/// view model can call <see cref="EnsureUpToDateAsync"/> to gate a write
/// operation. While <see cref="IsUpdateRequired"/> is true the database is
/// effectively read-only: content mutations (imports, loads into project,
/// edits, deletes, version management) must be gated.
/// </summary>
public interface IDatabaseUpdateStateService
{
    /// <summary>True when at least one registered CRITICAL task reports pending records — processable OR newer-only (ADR-054 §3a: the database stays read-only until perfectly updated).</summary>
    bool IsUpdateRequired { get; }

    /// <summary>Total PROCESSABLE pending records across all CRITICAL tasks (for the "update now" offer).</summary>
    int PendingCount { get; }

    /// <summary>
    /// Total pending records across all OPTIONAL tasks (ADR-054).
    /// Drives the database-tools menu command only — never gates writes.
    /// </summary>
    int OptionalPendingCount { get; }

    /// <summary>
    /// CRITICAL pending records whose every file variant requires a NEWER
    /// Revit than the running one (ADR-054 §3a). They gate exactly like
    /// processable critical records, but the gate text points at
    /// <see cref="NewerOnlyRequiredRevitVersion"/> instead of offering an
    /// immediate update.
    /// </summary>
    int NewerOnlyCriticalCount { get; }

    /// <summary>
    /// Minimum Revit major version that makes ALL newer-only critical
    /// groups processable in one pass (0 when none). Shown in the gate and
    /// banner texts ("выполните обновление в Revit {0}+ — тогда всё
    /// обновится за один раз").
    /// </summary>
    int NewerOnlyRequiredRevitVersion { get; }

    /// <summary>
    /// Minimum Revit major version that makes ALL newer-only OPTIONAL
    /// groups processable in one pass (0 when none). Shown in the amber
    /// tooltip so the user knows where the recommended update can run.
    /// </summary>
    int NewerOnlyOptionalRequiredRevitVersion { get; }

    /// <summary>
    /// OPTIONAL pending records whose every file variant requires a NEWER
    /// Revit (ADR-054 §3a). Non-blocking: drives only the amber indicator —
    /// these records cannot be fixed in the running Revit anyway and do
    /// not affect write integrity.
    /// </summary>
    int NewerOnlyPendingCount { get; }

    /// <summary>True while migrations are running.</summary>
    bool IsRunning { get; }

    /// <summary>Raised whenever any of the state properties changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Recomputes the pending count for the active database. No-op-safe to
    /// call often; the heavy work is a cheap SQL COUNT per migration.
    /// </summary>
    Task RefreshAsync(int revitMajorVersion, CancellationToken ct = default);

    /// <summary>Clears the state (no active database).</summary>
    void Reset();

    /// <summary>
    /// Gate for write operations: when no update is required returns true
    /// immediately; otherwise shows the explanation dialog and, on "Yes",
    /// runs the migrations. Returns true when the caller may proceed
    /// (database is up to date).
    /// </summary>
    Task<bool> EnsureUpToDateAsync();

    /// <summary>
    /// Runs the actualization engine over ALL pending groups (the single
    /// unified "Обновить базу" command, ADR-054): one file open per family,
    /// pending tasks applied by Order. Refreshes the state afterwards.
    /// Errors are logged, not thrown — committed records survive and the
    /// next run resumes.
    /// </summary>
    Task UpdateAsync();
}
