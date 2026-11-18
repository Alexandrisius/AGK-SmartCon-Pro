using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

public enum AgentProgressKind
{
    Status = 0,
    TextDelta = 1,
    ReasoningDelta = 2,
    ToolStarted = 3,
    ToolCompleted = 4,
    TurnCompleted = 5,
    Error = 6,
    ContextUsage = 7
}

public sealed class AgentProgress
{
    public AgentProgressKind Kind { get; init; }

    public string? Text { get; init; }

    public string? Details { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public bool Success { get; init; }

    public string? Error { get; init; }

    public AgentUsage? Usage { get; init; }

    public int ToolCallCount { get; init; }

    /// <summary>Estimated prompt tokens of the current round.</summary>
    public int ContextTokens { get; init; }

    /// <summary>Configured compaction trigger shown to the operator.</summary>
    public int ContextLimit { get; init; }

    public string? CompactionKind { get; init; }

    public int CompactionMasked { get; init; }

    public int CompactionDropped { get; init; }

    public int CompactionTokensBefore { get; init; }

    public int CompactionTokensAfter { get; init; }

    public static AgentProgress Status(string text) =>
        new() { Kind = AgentProgressKind.Status, Text = text };

    public static AgentProgress TextDelta(string text) =>
        new() { Kind = AgentProgressKind.TextDelta, Text = text };

    public static AgentProgress ReasoningDelta(string text) =>
        new() { Kind = AgentProgressKind.ReasoningDelta, Text = text };

    public static AgentProgress ContextUsage(
        int contextTokens,
        int contextLimit,
        AgentCompactionResult? compaction)
    {
        var changed = compaction is not null && compaction.Changed;
        return new AgentProgress
        {
            Kind = AgentProgressKind.ContextUsage,
            ContextTokens = contextTokens,
            ContextLimit = contextLimit,
            CompactionKind = !changed
                ? null
                : compaction!.Summarized
                    ? "summary"
                    : compaction.Fallback && compaction.DroppedCount > 0
                        ? "hard_shrink"
                        : "masking",
            CompactionMasked = changed ? compaction!.MaskedCount : 0,
            CompactionDropped = changed ? compaction!.DroppedCount : 0,
            CompactionTokensBefore = changed ? compaction!.TokensBefore : 0,
            CompactionTokensAfter = changed ? compaction!.TokensAfter : 0
        };
    }
}