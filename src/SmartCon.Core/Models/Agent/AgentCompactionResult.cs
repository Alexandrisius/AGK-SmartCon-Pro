namespace SmartCon.Core.Models.Agent;

/// <summary>Output of one context-compaction pass.</summary>
public sealed class AgentCompactionResult
{
    public required List<AgentMessage> Messages { get; init; }

    public string? Summary { get; init; }

    public int MaskedCount { get; init; }

    public int DroppedCount { get; init; }

    /// <summary>
    /// Session ordinal of the last raw message covered by the checkpoint, or
    /// the request's <c>BaseRowId</c> when nothing was covered. The orchestrator
    /// advances its checkpoint anchor with this value after a summary.
    /// </summary>
    public int CoveredToRowId { get; init; }

    public int TokensBefore { get; init; }

    public int TokensAfter { get; init; }

    public bool Summarized { get; init; }

    /// <summary>True when the summary step failed and the ladder fell back.</summary>
    public bool Fallback { get; init; }

    public string? FallbackReason { get; init; }

    public AgentCompactionRecord? Record { get; init; }

    /// <summary>
    /// Packs whose skill markdown is no longer delivered verbatim by any message
    /// of the projection (masked into a digest or dropped with the covered
    /// prefix). The orchestrator unmarks them in <c>SentPackSkills</c>, so the
    /// next load_tool_pack or the next write of a pack tool re-sends the manual
    /// instead of silently working without it (#455).
    /// </summary>
    public IReadOnlyList<string> SkillPacksToResend { get; init; } = Array.Empty<string>();

    public bool Changed => MaskedCount > 0 || DroppedCount > 0 || Summarized;
}
