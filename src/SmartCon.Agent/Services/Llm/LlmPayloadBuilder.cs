using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services.Llm;

internal static class LlmPayloadBuilder
{
    internal static Dictionary<string, object?> Build(AgentChatRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages.Select(BuildMessage).ToList(),
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true }
        };

        if (request.Tools.Count > 0)
        {
            // Canonical name order: the tool block must be byte-identical between
            // requests with the same set, otherwise the provider cache misses (P.4).
            payload["tools"] = request.Tools
                .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .Select(BuildTool)
                .ToList();
        }

        if (request.Temperature.HasValue)
            payload["temperature"] = request.Temperature.Value;

        if (request.MaxOutputTokens.HasValue)
            payload["max_tokens"] = request.MaxOutputTokens.Value;

        return payload;
    }

    private static object BuildMessage(AgentMessage message)
    {
        switch (message.Role)
        {
            case AgentRole.System:
                return new Dictionary<string, object?>
                {
                    ["role"] = "system",
                    ["content"] = message.Content ?? string.Empty
                };

            case AgentRole.User:
                return new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = BuildUserContent(message)
                };

            case AgentRole.Assistant:
                return BuildAssistantMessage(message);

            case AgentRole.Tool:
                return new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = message.ToolCallId ?? string.Empty,
                    ["content"] = message.Content ?? string.Empty
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(message), message.Role, "Unknown agent role.");
        }
    }

    private static object BuildUserContent(AgentMessage message)
    {
        if (message.Attachments.Count == 0)
            return message.Content ?? string.Empty;

        var parts = new List<object>();
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            parts.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = message.Content
            });
        }

        foreach (var attachment in message.Attachments)
        {
            parts.Add(new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = "data:" + attachment.MimeType + ";base64," + attachment.Base64Data
                }
            });
        }

        return parts;
    }

    private static object BuildAssistantMessage(AgentMessage message)
    {
        var result = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = string.IsNullOrEmpty(message.Content) ? null : message.Content
        };

        if (message.ToolCalls.Count > 0)
        {
            result["tool_calls"] = message.ToolCalls.Select(call => new Dictionary<string, object?>
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson
                }
            }).ToList();
        }

        if (!string.IsNullOrEmpty(message.ReasoningContent))
            result["reasoning_content"] = message.ReasoningContent;

        return result;
    }

    private static object BuildTool(AgentToolDefinition tool)
    {
        using var document = JsonDocument.Parse(tool.InputSchemaJson);
        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = document.RootElement.Clone()
            }
        };
    }
}