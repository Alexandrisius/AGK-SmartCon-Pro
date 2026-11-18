using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

/// <summary>
/// Stores full tool results that were trimmed before reaching the model.
/// Reads are scoped to the owning conversation.
/// </summary>
public interface IAgentToolResultBlobStore
{
    Task SaveAsync(Guid sessionId, string toolCallId, string resultJson, CancellationToken ct);

    Task<AgentToolResultBlob?> GetAsync(
        Guid sessionId,
        string toolCallId,
        CancellationToken ct);
}
