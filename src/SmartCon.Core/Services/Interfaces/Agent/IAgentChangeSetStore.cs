using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentChangeSetStore
{
    Task<Guid> BeginAsync(ChangeSetHeader header, CancellationToken ct);

    Task AddItemsAsync(Guid changeSetId, IReadOnlyList<ChangeItem> items, CancellationToken ct);

    Task MarkAppliedAsync(Guid changeSetId, DateTime appliedAt, CancellationToken ct);

    /// <summary>
    /// Headers of the document's change sets in reverse-chronological order
    /// (newest first). Implementations cap <paramref name="limit"/>; pass
    /// <paramref name="offset"/> to read the next page.
    /// </summary>
    Task<IReadOnlyList<ChangeSetHeader>> ListAsync(
        string documentId,
        int limit,
        int offset,
        CancellationToken ct);

    Task<ChangeSetDetails?> GetAsync(Guid changeSetId, CancellationToken ct);

    Task<ChangeSetDetails?> GetLastAppliedAsync(string documentId, CancellationToken ct);

    /// <summary>
    /// Marks a change set as rolled back: the header is kept for the audit trail
    /// but it is no longer reported by <see cref="GetLastAppliedAsync"/>.
    /// </summary>
    Task MarkRolledBackAsync(Guid changeSetId, DateTime rolledBackAt, CancellationToken ct);

    /// <summary>
    /// Closes a change set without touching the model (C2). Used when its undo is
    /// permanently blocked by a name/id conflict: the set stops shadowing older
    /// sets so the next <c>undo_last</c> targets the previous applied set.
    /// </summary>
    Task MarkDiscardedAsync(Guid changeSetId, DateTime discardedAt, CancellationToken ct);
}
