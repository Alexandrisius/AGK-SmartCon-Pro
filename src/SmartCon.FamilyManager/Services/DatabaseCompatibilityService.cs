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
///
/// Developer escape hatch (ADR-058 §5): DEBUG builds keep the gate OFF by
/// default so the developer-owner is never locked out of his own databases
/// (a local build's version is the stable <c>Version.txt</c> one, older
/// than any beta floor). The environment variable
/// <c>SMARTCON_FM_COMPAT_GATE</c> flips the behavior in both directions:
/// <c>force</c> re-enables the gate in a DEBUG build (gate testing),
/// <c>disable</c> suppresses it even in RELEASE (support emergency hatch).
/// </summary>
public sealed class DatabaseCompatibilityService : IDatabaseCompatibilityService
{
    internal const string GateOverrideEnvVar = "SMARTCON_FM_COMPAT_GATE";

    internal enum OverrideMode
    {
        Default,
        Force,
        Disable,
    }

    private readonly LocalCatalogDatabase _database;
    private readonly IUpdateService _updateService;
    private readonly OverrideMode _override;

    public DatabaseCompatibilityService(LocalCatalogDatabase database, IUpdateService updateService)
        : this(database, updateService, ReadOverrideFromEnvironment())
    {
    }

    internal DatabaseCompatibilityService(LocalCatalogDatabase database, IUpdateService updateService, OverrideMode gateOverride)
    {
        _database = database;
        _updateService = updateService;
        _override = gateOverride;

        if (!IsGateEffectivelyEnabled())
        {
            SmartConLogger.Info(_override == OverrideMode.Disable
                ? $"DbCompat: compat gate suppressed via {GateOverrideEnvVar}=disable"
                : $"DbCompat: compat gate off (DEBUG build default) — set {GateOverrideEnvVar}=force to enable it");
        }
        else if (_override == OverrideMode.Force)
        {
            SmartConLogger.Info($"DbCompat: compat gate force-enabled via {GateOverrideEnvVar}=force");
        }
    }

    public bool IsDatabaseNewerThanPlugin { get; private set; }

    public string? DatabaseMinPluginVersion { get; private set; }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("DbCompat",
            ("Method", nameof(RefreshAsync)));

        DatabaseMinPluginVersion = await ReadMinPluginVersionAsync(ct).ConfigureAwait(false);
        var rawGate = EvaluateGate(DatabaseMinPluginVersion);

        if (rawGate && !IsGateEffectivelyEnabled())
        {
            IsDatabaseNewerThanPlugin = false;
            if (_override == OverrideMode.Disable)
            {
                SmartConLogger.Warn(
                    $"DbCompat: database requires SmartCon >= {DatabaseMinPluginVersion} but the compat gate is suppressed via {GateOverrideEnvVar}=disable — writes are NOT blocked. " +
                    $"[Action: remove {GateOverrideEnvVar} to restore the gate]");
            }
            else
            {
                SmartConLogger.Debug(
                    $"DbCompat: database requires SmartCon >= {DatabaseMinPluginVersion} — gate off (DEBUG default), writes allowed");
            }
            return;
        }

        IsDatabaseNewerThanPlugin = rawGate;

        SmartConLogger.Info(IsDatabaseNewerThanPlugin
            ? $"Database requires SmartCon >= {DatabaseMinPluginVersion}; current plugin is older — connecting read-only"
            : $"Database compatibility OK (min_plugin_version='{DatabaseMinPluginVersion ?? "<none>"}')");
    }

    public void Reset()
    {
        DatabaseMinPluginVersion = null;
        IsDatabaseNewerThanPlugin = false;
    }

    private bool IsGateEffectivelyEnabled() => _override switch
    {
        OverrideMode.Force => true,
        OverrideMode.Disable => false,
#if DEBUG
        _ => false,
#else
        _ => true,
#endif
    };

    private static OverrideMode ReadOverrideFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(GateOverrideEnvVar);
        return raw?.Trim().ToLowerInvariant() switch
        {
            "force" or "1" or "true" => OverrideMode.Force,
            "disable" or "0" or "false" => OverrideMode.Disable,
            _ => OverrideMode.Default,
        };
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
