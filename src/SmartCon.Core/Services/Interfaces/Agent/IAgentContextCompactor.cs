using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

/// <summary>
/// Context ladder (runbook H1-16): mask old tool results, then replace old
/// history with a checkpoint summary, then hard-shrink to the recent tail.
/// </summary>
public interface IAgentContextCompactor
{
    /// <summary>
    /// Turn-start projection: replaces old tool results with recoverable
    /// digests when that saves a meaningful amount of context. Runs regardless
    /// of the trigger so the prompt does not balloon back to the raw history on
    /// every turn.
    /// </summary>
    Task<AgentCompactionResult> ProjectAsync(
        AgentCompactionRequest request,
        CancellationToken ct);

    Task<AgentCompactionResult> CompactAsync(
        AgentCompactionRequest request,
        CancellationToken ct);
}
