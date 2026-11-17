using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// In-memory change-set store for integration tests. The SQLite store lives in
/// SmartCon.Agent (WPF project) and must not be referenced from the test host;
/// SQLite persistence itself is covered by unit tests.
/// </summary>
internal sealed class InMemoryAgentChangeSetStore : IAgentChangeSetStore
{
    private readonly Dictionary<Guid, ChangeSetDetails> _sets = new();

    public IReadOnlyList<ChangeSetDetails> All => _sets.Values.ToList();

    public ChangeSetDetails? Last => _sets.Count == 0
        ? null
        : _sets.Values.OrderByDescending(set => set.Header.CreatedAt).First();

    public Task<Guid> BeginAsync(ChangeSetHeader header, CancellationToken ct)
    {
        var id = header.Id == Guid.Empty ? Guid.NewGuid() : header.Id;
        _sets[id] = new ChangeSetDetails
        {
            Header = new ChangeSetHeader
            {
                Id = id,
                ConversationId = header.ConversationId,
                ToolCallId = header.ToolCallId,
                Operation = header.Operation,
                DocumentId = header.DocumentId,
                DocumentPath = header.DocumentPath,
                RevitVersion = header.RevitVersion,
                Author = header.Author,
                Description = header.Description,
                CreatedAt = header.CreatedAt
            }
        };
        return Task.FromResult(id);
    }

    public Task AddItemsAsync(Guid changeSetId, IReadOnlyList<ChangeItem> items, CancellationToken ct)
    {
        if (!_sets.TryGetValue(changeSetId, out var existing))
            return Task.CompletedTask;

        var merged = existing.Items.Concat(items).ToList();
        _sets[changeSetId] = new ChangeSetDetails { Header = existing.Header, Items = merged };
        return Task.CompletedTask;
    }

    public Task MarkAppliedAsync(Guid changeSetId, DateTime appliedAt, CancellationToken ct)
    {
        if (!_sets.TryGetValue(changeSetId, out var existing))
            return Task.CompletedTask;

        _sets[changeSetId] = new ChangeSetDetails
        {
            Header = new ChangeSetHeader
            {
                Id = existing.Header.Id,
                ConversationId = existing.Header.ConversationId,
                ToolCallId = existing.Header.ToolCallId,
                Operation = existing.Header.Operation,
                DocumentId = existing.Header.DocumentId,
                DocumentPath = existing.Header.DocumentPath,
                RevitVersion = existing.Header.RevitVersion,
                Author = existing.Header.Author,
                Description = existing.Header.Description,
                CreatedAt = existing.Header.CreatedAt,
                AppliedAt = appliedAt
            },
            Items = existing.Items
        };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChangeSetHeader>> ListAsync(
        string documentId,
        int limit,
        CancellationToken ct)
    {
        var headers = _sets.Values
            .Where(set => set.Header.DocumentId == documentId)
            .OrderByDescending(set => set.Header.CreatedAt)
            .Take(Math.Max(1, limit))
            .Select(set => set.Header)
            .ToList();

        return Task.FromResult<IReadOnlyList<ChangeSetHeader>>(headers);
    }

    public Task<ChangeSetDetails?> GetAsync(Guid changeSetId, CancellationToken ct) =>
        Task.FromResult(_sets.TryGetValue(changeSetId, out var found) ? found : null);

    public Task<ChangeSetDetails?> GetLastAppliedAsync(string documentId, CancellationToken ct)
    {
        var found = _sets.Values
            .Where(set => set.Header.DocumentId == documentId && set.Header.AppliedAt.HasValue)
            .OrderByDescending(set => set.Header.AppliedAt)
            .FirstOrDefault();

        return Task.FromResult(found);
    }

    public Task MarkRolledBackAsync(Guid changeSetId, DateTime rolledBackAt, CancellationToken ct)
    {
        if (!_sets.TryGetValue(changeSetId, out var existing))
            return Task.CompletedTask;

        _sets[changeSetId] = new ChangeSetDetails
        {
            Header = new ChangeSetHeader
            {
                Id = existing.Header.Id,
                ConversationId = existing.Header.ConversationId,
                ToolCallId = existing.Header.ToolCallId,
                Operation = existing.Header.Operation,
                DocumentId = existing.Header.DocumentId,
                DocumentPath = existing.Header.DocumentPath,
                RevitVersion = existing.Header.RevitVersion,
                Author = existing.Header.Author,
                Description = existing.Header.Description,
                CreatedAt = existing.Header.CreatedAt,
                AppliedAt = null
            },
            Items = existing.Items
                .Select(item => new ChangeItem
                {
                    Id = item.Id,
                    ChangeSetId = item.ChangeSetId,
                    UniqueId = item.UniqueId,
                    ElementId = item.ElementId,
                    ParameterName = item.ParameterName,
                    OldValue = item.OldValue,
                    NewValue = item.NewValue,
                    Status = ChangeStatus.RolledBack,
                    Error = item.Error
                })
                .ToList()
        };
        return Task.CompletedTask;
    }
}
