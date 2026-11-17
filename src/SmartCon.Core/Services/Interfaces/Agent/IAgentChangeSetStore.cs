using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentChangeSetStore
{
    Task<Guid> BeginAsync(ChangeSetHeader header, CancellationToken ct);

    Task AddItemsAsync(Guid changeSetId, IReadOnlyList<ChangeItem> items, CancellationToken ct);

    Task MarkAppliedAsync(Guid changeSetId, DateTime appliedAt, CancellationToken ct);

    Task<IReadOnlyList<ChangeSetHeader>> ListAsync(string documentId, int limit, CancellationToken ct);

    Task<ChangeSetDetails?> GetAsync(Guid changeSetId, CancellationToken ct);

    Task<ChangeSetDetails?> GetLastAppliedAsync(string documentId, CancellationToken ct);

    /// <summary>
    /// Marks a change set as rolled back: the header is kept for the audit trail
    /// but it is no longer reported by <see cref="GetLastAppliedAsync"/>.
    /// </summary>
    Task MarkRolledBackAsync(Guid changeSetId, DateTime rolledBackAt, CancellationToken ct);
}