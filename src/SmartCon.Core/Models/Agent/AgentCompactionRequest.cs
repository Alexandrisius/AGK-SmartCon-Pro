namespace SmartCon.Core.Models.Agent;

/// <summary>Input of one context-compaction pass (runbook H1-15/H1-16).</summary>
public sealed class AgentCompactionRequest
{
    public required IReadOnlyList<AgentMessage> Messages { get; init; }

    public int ContextBudgetTokens { get; init; } = 500_000;

    public double Threshold { get; init; } = 0.7;

    /// <summary>Newest tool results always kept verbatim.</summary>
    public int KeepRecentToolResults { get; init; } = 6;

    /// <summary>Newest conversation messages always kept verbatim.</summary>
    public int KeepRecentMessages { get; init; } = 8;

    /// <summary>Messages kept after the hard-shrink step.</summary>
    public int HardShrinkMessages { get; init; } = 4;

    /// <summary>How many session messages are already persisted (checkpoint anchor).</summary>
    public int CoveredMessageCount { get; init; }

    public Guid SessionId { get; init; }

    public WorkingState? WorkingState { get; init; }

    public string? SummaryModel { get; init; }

    /// <summary>Token estimate including system prompt and tool schemas.</summary>
    public Func<IReadOnlyList<AgentMessage>, int>? Estimator { get; init; }
}
