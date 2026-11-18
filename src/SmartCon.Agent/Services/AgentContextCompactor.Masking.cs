using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

public sealed partial class AgentContextCompactor
{
    /// <summary>
    /// Tool results that are never masked: discovery/loading meta results, stored
    /// result reads and plan read-back are small and still needed verbatim.
    /// </summary>
    private static readonly HashSet<string> ProtectedTools =
        new(StringComparer.Ordinal)
        {
            "find_tools",
            "load_tool_pack",
            "read_tool_result",
            "execute_plan"
        };

    private static List<AgentMessage> MaskOldToolResults(
        IReadOnlyList<AgentMessage> messages,
        int keepRecent,
        out int maskedCount)
    {
        maskedCount = 0;
        var result = messages.ToList();
        var toolNames = BuildToolNameMap(messages);

        var toolIndexes = new List<int>();
        for (var index = 0; index < result.Count; index++)
        {
            if (result[index].Role == AgentRole.Tool)
                toolIndexes.Add(index);
        }

        var keep = Math.Max(0, keepRecent);
        var compactable = Math.Max(0, toolIndexes.Count - keep);
        for (var index = 0; index < compactable; index++)
        {
            var messageIndex = toolIndexes[index];
            var message = result[messageIndex];
            var toolName = message.ToolCallId is not null
                && toolNames.TryGetValue(message.ToolCallId, out var name)
                    ? name
                    : null;
            if (toolName is not null && ProtectedTools.Contains(toolName))
                continue;

            if (IsDigest(message.Content))
                continue;

            result[messageIndex] = AgentMessage
                .ToolResult(
                    message.ToolCallId ?? string.Empty,
                    BuildDigest(message.Content))
                .WithSessionIndex(message.SessionIndex);
            maskedCount++;
        }

        return result;
    }

    private static Dictionary<string, string> BuildToolNameMap(
        IReadOnlyList<AgentMessage> messages)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls)
            {
                if (!string.IsNullOrEmpty(call.Id))
                    map[call.Id] = call.Name;
            }
        }

        return map;
    }

    private static bool IsDigest(string? content) =>
        !string.IsNullOrEmpty(content)
        && content!.IndexOf("\"compacted\":true", StringComparison.Ordinal) >= 0;

    /// <summary>
    /// Digest keeps what the model needs to react (success/message/counts) and
    /// the stored-result handle when the original envelope had one.
    /// </summary>
    private static string BuildDigest(string? content)
    {
        var success = true;
        var affected = 0;
        string? message = null;
        string? resultHandle = null;

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
                        && affectedValue.TryGetInt32(out var parsed))
                    {
                        affected = parsed;
                    }

                    resultHandle = FindHandle(root);
                }
            }
            catch (JsonException)
            {
                // Not JSON: the digest stays generic.
            }
        }

        var handleHint = resultHandle is null
            ? "Re-run the tool with a narrower request if you still need the data."
            : "The full result is stored under resultHandle: call read_tool_result.";

        return JsonSerializer.Serialize(new
        {
            compacted = true,
            success,
            message = message ?? "Tool result compacted to fit the context budget.",
            affectedCount = affected,
            truncated = true,
            resultHandle,
            hint = handleHint
        });
    }

    private static string? FindHandle(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("resultHandle", out var nested)
            && nested.ValueKind == JsonValueKind.String)
        {
            return nested.GetString();
        }

        return root.TryGetProperty("resultHandle", out var direct)
            && direct.ValueKind == JsonValueKind.String
                ? direct.GetString()
                : null;
    }
}
