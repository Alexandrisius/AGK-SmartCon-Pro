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

    /// <summary>
    /// Newest conversation tail kept verbatim, in estimated tokens. When set,
    /// the tail is bounded by tokens instead of a fixed message count; the
    /// compactor still keeps at least <see cref="KeepRecentMessages"/>.
    /// </summary>
    public int KeepRecentTokens { get; init; }

    /// <summary>Messages kept after the hard-shrink step.</summary>
    public int HardShrinkMessages { get; init; } = 4;

    /// <summary>
    /// Tokens reserved for the ephemeral turn context and context-status block
    /// that are added after compaction. The effective trigger is
    /// <c>(ContextBudgetTokens - ReserveTokens) * Threshold</c>.
    /// </summary>
    public int ReserveTokens { get; init; }

    /// <summary>
    /// 1-based ordinal of the last session message before
    /// <see cref="Messages"/>. Used as the checkpoint anchor when a message does
    /// not carry its own <see cref="AgentMessage.SessionIndex"/>.
    /// </summary>
    public int BaseRowId { get; init; }

    /// <summary>
    /// Projection-only: skip masking when it saves fewer tokens than this.
    /// </summary>
    public int MinMaskSavingsTokens { get; init; }

    /// <summary>
    /// Raw persisted history of the conversation (1-based ordinals in
    /// <see cref="AgentMessage.SessionIndex"/>). Compaction uses it to hash the
    /// covered prefix exactly as it is stored, not as it is masked in memory.
    /// </summary>
    public IReadOnlyList<AgentMessage>? RawHistory { get; init; }

    /// <summary>
    /// Estimator without the provider-usage floor. The floor is only a trigger
    /// safeguard: it must not be applied after masking/summary, otherwise the
    /// shrunken prompt still looks as large as the pre-compaction one.
    /// </summary>
    public Func<IReadOnlyList<AgentMessage>, int>? PostEstimator { get; init; }

    /// <summary>
    /// False after a hard shrink that already dropped messages without a
    /// checkpoint: the in-memory list no longer maps to a contiguous raw prefix,
    /// so a new checkpoint would be invalid. The turn continues with masking.
    /// </summary>
    public bool AllowCheckpoint { get; init; } = true;

    public Guid SessionId { get; init; }

    public WorkingState? WorkingState { get; init; }

    public string? SummaryModel { get; init; }

    /// <summary>Token estimate including system prompt and tool schemas.</summary>
    public Func<IReadOnlyList<AgentMessage>, int>? Estimator { get; init; }
}
