using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

/// <summary>
/// Context ladder (runbook H1-16): mask old tool results, then replace old
/// history with a checkpoint summary, then hard-shrink to the recent tail.
/// </summary>
public interface IAgentContextCompactor
{
    Task<AgentCompactionResult> CompactAsync(
        AgentCompactionRequest request,
        CancellationToken ct);
}
