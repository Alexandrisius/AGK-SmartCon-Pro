using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentToolCallStore
{
    Task RecordAsync(AgentToolCallRecord record, CancellationToken ct);
}

/// <summary>Per-turn token usage, used by the context-budget indicator.</summary>
public interface IAgentUsageStore
{
    Task RecordAsync(AgentUsageRecord record, CancellationToken ct);

    /// <summary>
    /// Accumulated provider-reported cost of every recorded turn of one
    /// conversation, so the panel can show the session total instead of the
    /// cost of the last turn.
    /// </summary>
    Task<decimal> GetSessionCostAsync(Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Last measured context fill of one conversation, so the panel can show the
    /// donut immediately after a restart or a session switch. Null when the
    /// conversation has no measured turn yet.
    /// </summary>
    Task<AgentContextSnapshot?> GetLastContextAsync(Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Aggregates the token usage and tool-call tables for one conversation.
    /// Used by the chat analytics line; returns an empty aggregate when the
    /// conversation has no recorded turn yet.
    /// </summary>
    Task<AgentSessionMetrics?> GetSessionMetricsAsync(Guid sessionId, CancellationToken ct);
}

/// <summary>
/// Provider/model of the active LLM session, reported with tooling feedback.
/// Implemented in SmartCon.Agent so that tools outside that assembly can read it.
/// </summary>
public interface IAgentRuntimeInfo
{
    string Provider { get; }

    string Model { get; }
}
