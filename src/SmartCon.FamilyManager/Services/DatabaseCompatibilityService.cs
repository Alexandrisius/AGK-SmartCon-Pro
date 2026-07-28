using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Reads <c>database_meta.min_plugin_version</c> from the active catalog
/// database and compares it against the running plugin version
/// (ADR-058, #173). Fail-open by design: a missing/unreadable marker must
/// never lock users out — only a positively newer floor gates the database.
/// </summary>
public sealed class DatabaseCompatibilityService : IDatabaseCompatibilityService
{
    private readonly LocalCatalogDatabase _database;
    private readonly IUpdateService _updateService;

    public DatabaseCompatibilityService(LocalCatalogDatabase database, IUpdateService updateService)
    {
        _database = database;
        _updateService = updateService;
    }

    public bool IsDatabaseNewerThanPlugin { get; private set; }

    public string? DatabaseMinPluginVersion { get; private set; }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("DbCompat",
            ("Method", nameof(RefreshAsync)));

        DatabaseMinPluginVersion = await ReadMinPluginVersionAsync(ct).ConfigureAwait(false);
        IsDatabaseNewerThanPlugin = EvaluateGate(DatabaseMinPluginVersion);

        SmartConLogger.Info(IsDatabaseNewerThanPlugin
            ? $"Database requires SmartCon >= {DatabaseMinPluginVersion}; current plugin is older — connecting read-only"
            : $"Database compatibility OK (min_plugin_version='{DatabaseMinPluginVersion ?? "<none>"}')");
    }

    public void Reset()
    {
        DatabaseMinPluginVersion = null;
        IsDatabaseNewerThanPlugin = false;
    }

    private async Task<string?> ReadMinPluginVersionAsync(CancellationToken ct)
    {
        try
        {
            using var conn = _database.CreateConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT min_plugin_version FROM database_meta LIMIT 1";
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is null or DBNull ? null : Convert.ToString(value);
        }
        catch (SqliteException ex)
        {
            // Column missing on a pre-V24 database that has not been migrated
            // yet, or a transient read failure — never gate on guesswork.
            SmartConLogger.Warn(
                $"Could not read min_plugin_version: {ex.Message} [Action: none — treating the database as compatible]");
            return null;
        }
    }

    private bool EvaluateGate(string? minPluginVersion)
    {
        if (string.IsNullOrWhiteSpace(minPluginVersion))
            return false;

        if (!SemVersion.TryParse(minPluginVersion!, out var min))
        {
            SmartConLogger.Warn(
                $"Unparseable min_plugin_version '{minPluginVersion}' [Action: none — treating the database as compatible]");
            return false;
        }

        if (!SemVersion.TryParse(_updateService.GetCurrentVersion(), out var current))
            return false;

        return min > current;
    }
}
