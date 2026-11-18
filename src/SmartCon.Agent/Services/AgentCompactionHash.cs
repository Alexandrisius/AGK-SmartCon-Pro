using System.Globalization;
using System.Text;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

/// <summary>
/// Stable hash of the raw conversation prefix covered by a checkpoint. It is
/// computed from persisted messages only (<see cref="AgentMessage.SessionIndex"/>
/// greater than zero), so masking, restored checkpoint blocks and synthetic
/// tool-call repairs do not change it. On restore the orchestrator recomputes
/// the hash and ignores a checkpoint whose source prefix no longer matches.
/// </summary>
public static class AgentCompactionHash
{
    public static string Compute(IReadOnlyList<AgentMessage> messages)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));

        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            if (message.SessionIndex <= 0)
                continue;

            builder.Append(message.SessionIndex.ToString(CultureInfo.InvariantCulture))
                .Append('\u001f')
                .Append(((int)message.Role).ToString(CultureInfo.InvariantCulture))
                .Append('\u001f')
                .Append(message.ToolCallId ?? string.Empty)
                .Append('\u001f')
                .Append(message.Content ?? string.Empty)
                .Append('\u001f')
                .Append(message.ReasoningContent ?? string.Empty)
                .Append('\u001e');

            foreach (var call in message.ToolCalls)
            {
                builder.Append(call.Id)
                    .Append('\u001d')
                    .Append(call.Name)
                    .Append('\u001d')
                    .Append(call.ArgumentsJson)
                    .Append('\u001e');
            }

            builder.Append('\n');
        }

        return AgentHashing.Sha256Hex(builder.ToString());
    }

    /// <summary>
    /// Checks a persisted checkpoint against the raw history. Only the source
    /// slice that was summarized is hashed: a later checkpoint starts after the
    /// previous one, and the earlier part is inherited through the summary text,
    /// not re-summarized.
    /// </summary>
    public static bool Validate(
        AgentCompactionRecord checkpoint,
        IReadOnlyList<AgentMessage> history)
    {
        if (checkpoint is null || history is null)
            return false;

        if (checkpoint.ToRowId <= 0
            || checkpoint.ToRowId > history.Count
            || string.IsNullOrWhiteSpace(checkpoint.Summary))
        {
            return false;
        }

        var from = Math.Max(1, checkpoint.FromRowId);
        if (checkpoint.ToRowId < from)
            return false;

        var prefix = new List<AgentMessage>(checkpoint.ToRowId - from + 1);
        for (var index = from - 1; index < checkpoint.ToRowId; index++)
            prefix.Add(history[index]);

        return string.Equals(
            Compute(prefix),
            checkpoint.SourcePrefixHash,
            StringComparison.Ordinal);
    }
}