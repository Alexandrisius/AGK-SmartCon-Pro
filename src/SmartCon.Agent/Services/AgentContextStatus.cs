using System.Text;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

/// <summary>
/// Ephemeral status block appended to every request so the model sees the
/// current context usage and any compaction that happened in this round. It is
/// not persisted and is not part of the conversation history.
/// </summary>
public static class AgentContextStatus
{
    public const int EstimatedTokens = 96;

    /// <summary>
    /// Reminder appended after the tag when the previous answer of this round
    /// was discarded because the provider stopped at the output token limit.
    /// </summary>
    private const string OutputCutReminder =
        "Your previous reply was cut off by the output token limit before it was complete " +
        "(reasoning tokens count toward the limit). Send it again: at most 3 tool calls, " +
        "compact arguments, do not restate tool results.";

    public static string Build(
        int round,
        int elapsedMinutes,
        int contextTokens,
        int contextLimit,
        AgentCompactionResult? compaction,
        int outputCut = 0)
    {
        var builder = new StringBuilder(256);
        builder.Append("<context_status round=\"")
            .Append(round)
            .Append("\" elapsed_minutes=\"")
            .Append(elapsedMinutes)
            .Append("\" context_tokens=\"")
            .Append(contextTokens)
            .Append("\" context_limit=\"")
            .Append(contextLimit)
            .Append('"');

        if (compaction is not null && compaction.Changed)
        {
            var kind = compaction.Summarized
                ? "summary"
                : compaction.Fallback && compaction.DroppedCount > 0
                    ? "hard_shrink"
                    : "masking";
            builder.Append(" compacted=\"")
                .Append(kind)
                .Append("\" masked=\"")
                .Append(compaction.MaskedCount)
                .Append("\" dropped=\"")
                .Append(compaction.DroppedCount)
                .Append("\" tokens_before=\"")
                .Append(compaction.TokensBefore)
                .Append("\" tokens_after=\"")
                .Append(compaction.TokensAfter)
                .Append('"');
        }

        if (outputCut > 0)
            builder.Append(" output_cut=\"").Append(outputCut).Append('"');

        builder.Append(" />");
        if (outputCut > 0)
            builder.Append("\n").Append(OutputCutReminder);
        return builder.ToString();
    }

    public static AgentMessage BuildMessage(
        int round,
        int elapsedMinutes,
        int contextTokens,
        int contextLimit,
        AgentCompactionResult? compaction,
        int outputCut = 0) =>
        AgentMessage.System(Build(
            round, elapsedMinutes, contextTokens, contextLimit, compaction, outputCut));
}