using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

/// <summary>Persistence of conversation checkpoints (runbook H1-16).</summary>
public interface IAgentCompactionStore
{
    Task SaveAsync(AgentCompactionRecord record, CancellationToken ct);

    Task<AgentCompactionRecord?> GetLatestAsync(Guid sessionId, CancellationToken ct);
}
