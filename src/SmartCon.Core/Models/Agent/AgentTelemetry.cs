namespace SmartCon.Core.Models.Agent;

/// <summary>
/// One per-turn token usage row: provider usage plus turn metadata used by
/// cache/latency/cost analysis (H1-02).
/// </summary>
public sealed class AgentUsageRecord
{
    public string SessionId { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public AgentUsage Usage { get; init; } = AgentUsage.Empty;

    /// <summary>ok | cancelled | error</summary>
    public required string Status { get; init; }

    public long? LatencyMs { get; init; }

    public string? RequestId { get; init; }

    public string? CompactionId { get; init; }

    public int EstimatedPromptTokens { get; init; }

    /// <summary>Last measured context fill of the turn (panel donut numerator).</summary>
    public int ContextTokens { get; init; }

    /// <summary>Compaction trigger shown to the operator (panel donut denominator).</summary>
    public int ContextLimit { get; init; }
}

/// <summary>Last measured context fill of a conversation, restored on session switch.</summary>
public sealed class AgentContextSnapshot
{
    public int Tokens { get; init; }

    public int Limit { get; init; }
}

/// <summary>
/// Aggregated session metrics for the chat analytics line and for comparing agent
/// versions on the same task (H4.5): turns, tool calls, tokens and elapsed work.
/// </summary>
public sealed class AgentSessionMetrics
{
    public int Turns { get; init; }

    public int ToolCalls { get; init; }

    public int FailedToolCalls { get; init; }

    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public int ReasoningTokens { get; init; }

    public int CachedTokens { get; init; }

    public decimal CostUsd { get; init; }

    public long LlmLatencyMs { get; init; }

    public long ToolDurationMs { get; init; }

    public bool IsEmpty => Turns == 0 && ToolCalls == 0;
}

/// <summary>One executed tool call, persisted for friction analysis (Phase 3).</summary>
public sealed class AgentToolCallRecord
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    public required string ToolName { get; init; }

    public string? ArgumentsJson { get; init; }

    public string? ResultJson { get; init; }

    /// <summary>JSON array of argument repairs applied before the call (H1-09).</summary>
    public string? RepairsJson { get; init; }

    /// <summary>ok | error | cancelled</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    public long DurationMs { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

