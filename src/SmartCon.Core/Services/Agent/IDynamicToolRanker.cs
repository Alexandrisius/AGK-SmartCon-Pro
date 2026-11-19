using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Per-prompt ranking of the non-core catalog for the turn-start tool set:
/// the always-available hot set stays, the dynamic slots come from here.
/// Implemented by the decision-model router; when it is disabled or fails the
/// caller falls back to the deterministic card BM25 ranking.
/// </summary>
public interface IDynamicToolRanker
{
    bool IsEnabled { get; }

    Task<IReadOnlyList<AgentToolDefinition>> RankAsync(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int max,
        CancellationToken ct = default);
}