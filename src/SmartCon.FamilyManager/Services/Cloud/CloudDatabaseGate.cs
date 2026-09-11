using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>
/// In-memory snapshot of the ACTIVE database's <see cref="CloudLink"/>
/// (ADR-075 §7). Updated by <c>DatabaseManager</c> on every active-database
/// change and cloud-link mutation; read by <c>DbAccessControlService</c> to
/// AND the Subscribed read-only gate into every write decision and by the UI
/// to disable write commands. DI singleton — one instance per process.
/// </summary>
public sealed class CloudDatabaseGate
{
    private volatile CloudLink? _activeLink;

    /// <summary>Cloud link of the active database; null = purely local database.</summary>
    public CloudLink? ActiveLink => _activeLink;

    /// <summary>True when the active database is a Subscribed copy — writes blocked (sync is the only writer).</summary>
    public bool IsWriteBlocked => _activeLink?.Role == CloudLinkRole.Subscribed;

    public void Update(DatabaseConnection? activeConnection)
    {
        _activeLink = activeConnection?.CloudLink;
    }
}
