namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Forward-compatibility gate between the plugin and a FamilyManager catalog
/// database (ADR-058, #173). When a shared catalog.db was upgraded by a newer
/// SmartCon (breaking data change, e.g. a new content-hash format), older
/// plugins must not WRITE to it — their dedup/import logic would silently
/// corrupt the catalog (e.g. FHV2 plugin creating duplicates in an FHV3 db).
/// This service compares <c>database_meta.min_plugin_version</c> against the
/// running plugin version and exposes the result for UI (banner) and access
/// control (read-only enforcement).
/// </summary>
public interface IDatabaseCompatibilityService
{
    /// <summary>
    /// <c>true</c> when the active database requires a newer plugin than the
    /// running one. The database must be treated as read-only and the user
    /// directed to update the plugin.
    /// </summary>
    bool IsDatabaseNewerThanPlugin { get; }

    /// <summary>
    /// The <c>min_plugin_version</c> marker read from the active database,
    /// or <c>null</c> when none is set (older/legacy databases).
    /// </summary>
    string? DatabaseMinPluginVersion { get; }

    /// <summary>
    /// Re-read the marker from the currently active database and recompute
    /// <see cref="IsDatabaseNewerThanPlugin"/>. Call on connect/switch/init,
    /// before the RBAC role is re-resolved (the write-access decision ANDs
    /// this flag).
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>Clear the cached state (no active database).</summary>
    void Reset();
}
