using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentToolCallStore
{
    Task RecordAsync(AgentToolCallRecord record, CancellationToken ct);
}

public interface IAgentFeedbackStore
{
    Task RecordAsync(AgentFeedbackEntry entry, CancellationToken ct);

    Task<IReadOnlyList<AgentFeedbackEntry>> ListAsync(
        string? documentId,
        int limit,
        CancellationToken ct);
}

/// <summary>Per-turn token usage, used by the context-budget indicator.</summary>
public interface IAgentUsageStore
{
    Task RecordAsync(AgentUsageRecord record, CancellationToken ct);
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
