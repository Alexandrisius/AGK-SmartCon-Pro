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

