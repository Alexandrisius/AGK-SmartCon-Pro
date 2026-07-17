namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// A single catalog-database migration (the Issue #126 pattern, see
/// docs/architecture/database-migrations.md). Migrations are discovered
/// via DI (<c>IEnumerable&lt;IDatabaseMigration&gt;</c>), checked silently
/// after every database connect/switch, and run manually from the
/// database-tools menu ("Update database"). While any migration reports
/// pending records, load-into-project commands stay gated.
/// </summary>
/// <remarks>
/// Implementation requirements:
/// <list type="bullet">
/// <item><see cref="CountPendingAsync"/> must be cheap (SQL COUNT) and
/// side-effect free — it runs on every database switch.</item>
/// <item><see cref="RunAsync"/> must be resumable: interrupting and
/// re-running continues from the stop point (commit in chunks, track
/// completion per record — e.g. a marker column like
/// <c>hash_format_version</c>).</item>
/// <item>SQLite rules apply: DELETE journal mode, no WAL, connections
/// only via <c>LocalCatalogDatabase</c> (I-14).</item>
/// </list>
/// </remarks>
public interface IDatabaseMigration
{
    /// <summary>Stable unique id (e.g. <c>"hash-v2"</c>), used in logs.</summary>
    string Id { get; }

    /// <summary>
    /// Execution order when several migrations are pending (ascending).
    /// Schema-affecting migrations should use lower values than data repair.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Number of records still requiring this migration for the given Revit
    /// major version. Returns 0 when there is nothing to do.
    /// </summary>
    Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default);

    /// <summary>
    /// Runs the migration (an implementation typically shows its own
    /// progress UI). Called only when <see cref="CountPendingAsync"/>
    /// reported &gt; 0. Must tolerate cancellation mid-run and resume on
    /// the next call.
    /// </summary>
    Task RunAsync(int revitMajorVersion, CancellationToken ct = default);
}
