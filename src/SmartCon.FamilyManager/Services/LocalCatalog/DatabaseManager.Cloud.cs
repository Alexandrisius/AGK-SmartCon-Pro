using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// Cloud catalog link management (master plan §7.3.1, ADR-075 §7): registry
/// <c>cloudLink</c> marker + its duplicate in
/// <c>catalog.db.database_meta.remote_source_json</c> (self-heal source when
/// an older plugin rewrites registry.json without the unknown field).
/// </summary>
internal sealed partial class DatabaseManager
{
    public async Task<DatabaseConnection?> SetCloudLinkAsync(
        string connectionId,
        CloudLink? link,
        CancellationToken ct = default)
    {
        await _registryLock.WaitAsync(ct);
        try
        {
            var registry = await LoadRegistryAsync(ct);
            var conn = registry.Connections.FirstOrDefault(c => c.Id == connectionId);
            if (conn is null) return null;

            // Published — dual write: the local database is writable, mirror
            // the link into database_meta (self-heal source). Subscribed —
            // the manifest applier wrote the DB side during sync; only the
            // registry entry changes here (sync is the only writer of the copy).
            if (link is null || link.Role == CloudLinkRole.Published)
                await WriteRemoteSourceAsync(conn.Path, link, ct);

            var updated = conn with { CloudLink = link };
            var connections = registry.Connections
                .Select(c => c.Id == connectionId ? updated : c)
                .ToList();
            await SaveRegistryAsync(new DatabaseConnectionRegistry(registry.ActiveConnectionId, connections), ct);

            if (registry.ActiveConnectionId == connectionId)
                _cloudGate.Update(updated);
            return updated;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    /// <summary>
    /// Self-heal the registry cloudLink of <paramref name="conn"/> from
    /// <c>database_meta.remote_source_json</c> (E27: an older plugin rewrote
    /// registry.json and dropped the unknown field). MUST be called under
    /// <c>_registryLock</c> with the registry connection list that is about to
    /// be (or was just) saved — heals by re-saving the registry.
    /// </summary>
    /// <returns>The connection with the healed link, or the input unchanged.</returns>
    private async Task<DatabaseConnection> HealCloudLinkAsync(
        List<DatabaseConnection> connections,
        DatabaseConnection conn,
        CancellationToken ct)
    {
        if (conn.CloudLink is not null) return conn;

        var json = await TryReadRemoteSourceAsync(conn.Path, ct);
        var link = CloudLinkJson.TryDeserialize(json);
        if (link is null) return conn;

        using var _scope = SmartConLogger.BeginScope("DatabaseManager",
            ("Method", nameof(HealCloudLinkAsync)),
            ("BaseName", conn.Name),
            ("Slug", link.Slug));
        SmartConLogger.Info(
            $"CloudLink self-healed from database_meta.remote_source_json (role={link.Role}, slug={link.Slug})");

        var healed = conn with { CloudLink = link };
        for (var i = 0; i < connections.Count; i++)
        {
            if (connections[i].Id == conn.Id)
                connections[i] = healed;
        }
        await SaveRegistryAsync(new DatabaseConnectionRegistry(conn.Id, connections), ct);
        return healed;
    }

    /// <summary>Duplicate-write of the link into database_meta.remote_source_json (V39).</summary>
    private async Task WriteRemoteSourceAsync(string databaseRoot, CloudLink? link, CancellationToken ct)
    {
        var dbFile = Path.Combine(databaseRoot, "catalog.db");
        if (!File.Exists(dbFile))
            throw new FileNotFoundException(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbNotFoundAtPath) ?? string.Empty,
                dbFile);

        using var connection = _catalogDatabase.CreateConnectionForPath(dbFile);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE database_meta SET remote_source_json = @json";
        cmd.Parameters.Add(new SqliteParameter("@json",
            link is null ? DBNull.Value : CloudLinkJson.Serialize(link)));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort read of database_meta.remote_source_json. Null when the
    /// file/column is absent (pre-V39 database never migrated by this plugin)
    /// or unreadable — nothing to heal from.
    /// </summary>
    private async Task<string?> TryReadRemoteSourceAsync(string databaseRoot, CancellationToken ct)
    {
        try
        {
            var dbFile = Path.Combine(databaseRoot, "catalog.db");
            if (!File.Exists(dbFile)) return null;
            using var connection = _catalogDatabase.CreateConnectionForPath(dbFile);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT remote_source_json FROM database_meta LIMIT 1";
            return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            return null;
        }
    }
}
