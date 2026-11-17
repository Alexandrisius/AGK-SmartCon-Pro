using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentConversationStore
{
    Task<Guid> CreateConversationAsync(AgentConversationHeader header, CancellationToken ct);

    Task<IReadOnlyList<AgentConversationHeader>> ListConversationsAsync(
        string documentId,
        int limit,
        CancellationToken ct);

    Task<AgentConversationHeader?> GetConversationAsync(Guid conversationId, CancellationToken ct);

    Task AppendMessageAsync(Guid conversationId, AgentMessage message, CancellationToken ct);

    Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(Guid conversationId, CancellationToken ct);

    Task UpdateTitleAsync(Guid conversationId, string title, CancellationToken ct);

    Task DeleteConversationAsync(Guid conversationId, CancellationToken ct);
}