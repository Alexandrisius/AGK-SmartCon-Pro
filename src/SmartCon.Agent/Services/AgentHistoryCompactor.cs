using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

/// <summary>
/// Replaces old tool results with a compact digest once the conversation exceeds
/// the configured share of the context window (mvp-plan §11.1 "Compaction").
/// Tool-result clearing comes first because it is cheaper and safer than a
/// summary; full payloads remain in agent.db.
/// </summary>
public static class AgentHistoryCompactor
{
    public const double DefaultThreshold = 1.0;
    public const int KeepRecentToolResults = 4;
    public const int DefaultContextBudgetTokens = 500_000;

    public static (List<AgentMessage> Messages, int CompactedCount) Compact(
        IReadOnlyList<AgentMessage> messages,
        int contextBudgetTokens = DefaultContextBudgetTokens,
        double threshold = DefaultThreshold,
        int keepRecent = KeepRecentToolResults,
        Func<IReadOnlyList<AgentMessage>, int>? estimator = null)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));

        var result = new List<AgentMessage>(messages);
        var budget = Math.Max(1000, contextBudgetTokens);
        var limit = (int)(budget * threshold);
        var estimated = estimator is not null
            ? estimator(messages)
            : AgentTokenEstimator.Estimate(messages);
        if (estimated <= limit)
            return (result, 0);

        var toolIndexes = new List<int>();
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].Role == AgentRole.Tool)
                toolIndexes.Add(i);
        }

        var compactable = toolIndexes.Count - Math.Max(0, keepRecent);
        if (compactable <= 0)
            return (result, 0);

        var compacted = 0;
        for (var i = 0; i < compactable; i++)
        {
            var index = toolIndexes[i];
            var message = result[index];
            result[index] = AgentMessage.ToolResult(
                message.ToolCallId ?? string.Empty,
                BuildDigest(message.Content));
            compacted++;
        }

        SmartConLogger.Info(
            "Agent history compacted: " + compacted + " old tool result(s) replaced by a digest " +
            "(estimated " + estimated + " tokens > " + limit + " of " + budget + ")");

        return (result, compacted);
    }

    private static string BuildDigest(string? content)
    {
        var success = true;
        var affected = 0;
        string? message = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                using var document = JsonDocument.Parse(content!);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("success", out var successValue)
                        && successValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        success = successValue.GetBoolean();
                    }

                    if (root.TryGetProperty("message", out var messageValue)
                        && messageValue.ValueKind == JsonValueKind.String)
                    {
                        var text = messageValue.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            message = text!.Length <= 300 ? text : text.Substring(0, 300) + "…";
                        }
                    }

                    if (root.TryGetProperty("affectedCount", out var affectedValue)
                        && affectedValue.ValueKind == JsonValueKind.Number
                        && affectedValue.TryGetInt32(out var parsedAffected))
                    {
                        affected = parsedAffected;
                    }
                }
            }
            catch (JsonException)
            {
                // The stored payload is not JSON — the digest stays generic.
            }
        }

        var digest = new
        {
            compacted = true,
            success,
            message = message ?? "Tool result compacted to fit the context budget.",
            affectedCount = affected,
            truncated = true,
            hint = "The full result is no longer in the conversation. Re-run the tool " +
                   "with a narrower request if you still need the data."
        };

        return JsonSerializer.Serialize(digest);
    }
}
