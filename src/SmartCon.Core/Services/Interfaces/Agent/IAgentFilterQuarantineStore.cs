using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

/// <summary>
/// Quarantine of deleted view filters (S.26): a full snapshot of every filter
/// (rules, categories, applied views with overrides) is persisted to agent.db
/// before it is deleted, because a deleted filter cannot be restored by
/// undo_last. Snapshots are pruned on write after 90 days; restoration is a
/// manual task driven by the stored JSON.
/// </summary>
public interface IAgentFilterQuarantineStore
{
    Task SaveAsync(AgentFilterSnapshot snapshot, CancellationToken ct);

    Task<AgentFilterSnapshot?> GetAsync(string snapshotId, CancellationToken ct);

    /// <summary>Deletes snapshots older than the cutoff; returns the removed row count.</summary>
    Task<int> CleanupAsync(DateTime cutoffUtc, CancellationToken ct);
}
