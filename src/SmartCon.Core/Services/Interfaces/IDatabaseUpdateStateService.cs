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
    /// <summary>True when at least one registered migration reports pending records.</summary>
    bool IsUpdateRequired { get; }

    /// <summary>Total pending records across all migrations (for messages).</summary>
    int PendingCount { get; }

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
    /// Runs all pending migrations unconditionally (the "Update database"
    /// command) and refreshes the state. Errors are logged, not thrown —
    /// committed batches survive and the next run resumes.
    /// </summary>
    Task UpdateAsync();
}
