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

    public static string Build(
        int round,
        int elapsedMinutes,
        int contextTokens,
        int contextLimit,
        AgentCompactionResult? compaction)
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

        builder.Append(" />");
        return builder.ToString();
    }

    public static AgentMessage BuildMessage(
        int round,
        int elapsedMinutes,
        int contextTokens,
        int contextLimit,
        AgentCompactionResult? compaction) =>
        AgentMessage.System(Build(round, elapsedMinutes, contextTokens, contextLimit, compaction));
}