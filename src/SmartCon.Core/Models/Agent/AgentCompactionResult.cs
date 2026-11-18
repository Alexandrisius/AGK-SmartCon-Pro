namespace SmartCon.Core.Models.Agent;

/// <summary>Output of one context-compaction pass.</summary>
public sealed class AgentCompactionResult
{
    public required List<AgentMessage> Messages { get; init; }

    public string? Summary { get; init; }

    public int MaskedCount { get; init; }

    public int DroppedCount { get; init; }

    public int TokensBefore { get; init; }

    public int TokensAfter { get; init; }

    public bool Summarized { get; init; }

    /// <summary>True when the summary step failed and the ladder fell back.</summary>
    public bool Fallback { get; init; }

    public string? FallbackReason { get; init; }

    public AgentCompactionRecord? Record { get; init; }

    public bool Changed => MaskedCount > 0 || DroppedCount > 0 || Summarized;
}
