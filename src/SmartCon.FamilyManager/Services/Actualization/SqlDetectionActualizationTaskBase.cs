using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// Base class for <see cref="IDatabaseActualizationTask"/> implementations
/// whose pending criterion is a single SQL "column emptiness" fragment over
/// <c>catalog_versions</c> ⨝ <c>catalog_items</c>, grouped by the engine's
/// group key (<c>catalog_item_id|version_label</c>) — the common case
/// (ADR-054). The three detection methods are implemented once here from
/// <see cref="DetectionSql"/>; a concrete task declares only
/// <see cref="Id"/>, <see cref="Order"/>, <see cref="IsCritical"/>,
/// <see cref="DetectionSql"/> and <see cref="ApplyAsync"/>.
/// <para>
/// Deviating tasks override individual members instead of copying the
/// template: <see cref="HashFormatActualizationTask"/> overrides
/// <see cref="ApplyAsync"/> (recompute + item re-sync + floor bump) and
/// <see cref="HandleGroupFailureAsync"/> (terminal markers);
/// <see cref="MiniProjectMarkerActualizationTask"/> overrides
/// <see cref="RequiresExtraction"/> (file-level: owns open/save). The
/// default <see cref="RunFileFreePassAsync"/> is 0 and the default
/// <see cref="HandleGroupFailureAsync"/> keeps the group pending for a
/// retry on the next run.
/// </para>
/// </summary>
internal abstract class SqlDetectionActualizationTaskBase : IDatabaseActualizationTask
{
    private readonly LocalCatalogDatabase _database;

    protected SqlDetectionActualizationTaskBase(LocalCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>Connection factory for the task's own writes (I-14).</summary>
    protected LocalCatalogDatabase Database => _database;

    public abstract string Id { get; }
    public abstract int Order { get; }
    public abstract bool IsCritical { get; }

    /// <summary>Default: extraction-based task. File-level tasks (own
    /// open/save, e.g. <c>mini-project-marker-v1</c>) override to false.</summary>
    public virtual bool RequiresExtraction => true;

    /// <summary>
    /// FROM/JOIN/WHERE fragment selecting pending (<c>catalog_item_id</c>,
    /// <c>version_label</c>) groups — e.g.
    /// <c>FROM catalog_versions cv JOIN catalog_items ci ON ... WHERE ...</c>.
    /// Must be cheap and side-effect free (runs on every database switch),
    /// and the artifact written by <see cref="ApplyAsync"/> must clear it.
    /// </summary>
    protected abstract string DetectionSql { get; }

    public virtual async Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM (
                SELECT cv.catalog_item_id, cv.version_label
                {DetectionSql}
                  AND cv.revit_major_version <= @maxRevit
                GROUP BY cv.catalog_item_id, cv.version_label
            )
            """;
        cmd.Parameters.Add(new SqliteParameter("@maxRevit", revitMajorVersion));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? (int)l : 0;
    }

    public virtual async Task<NewerOnlyPendingInfo> GetNewerOnlyPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        // Openability is checked across ALL variants of the group — not
        // only the pending ones — because the engine applies the result to
        // every variant. RequiredRevitVersion = the minimum Revit making
        // ALL newer-only groups processable in one pass (MAX over groups
        // of the group's minimum variant version). Do not change casually.
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*), COALESCE(MAX((
                SELECT MIN(cv2.revit_major_version) FROM catalog_versions cv2
                WHERE cv2.catalog_item_id = p.itemId AND cv2.version_label = p.label)), 0)
            FROM (
                SELECT DISTINCT cv.catalog_item_id AS itemId, cv.version_label AS label
                {DetectionSql}
            ) p
            WHERE NOT EXISTS (
                SELECT 1 FROM catalog_versions cv3
                WHERE cv3.catalog_item_id = p.itemId AND cv3.version_label = p.label
                  AND cv3.revit_major_version <= @maxRevit
            )
            """;
        cmd.Parameters.Add(new SqliteParameter("@maxRevit", revitMajorVersion));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return NewerOnlyPendingInfo.None;
        return new NewerOnlyPendingInfo(reader.GetInt32(0), reader.GetInt32(1));
    }

    public virtual async Task<IReadOnlyCollection<string>> LoadPendingGroupKeysAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        // ALL pending groups, including newer-Revit-only — the engine
        // classifies openability and reports them as newer-pending.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT cv.catalog_item_id, cv.version_label
            {DetectionSql}
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0) + "|" + reader.GetString(1));
        }
        return keys;
    }

    public virtual Task<int> RunFileFreePassAsync(int revitMajorVersion, CancellationToken ct = default)
        => Task.FromResult(0);

    public abstract Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default);

    public virtual Task HandleGroupFailureAsync(
        ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default)
        => Task.CompletedTask;
}
