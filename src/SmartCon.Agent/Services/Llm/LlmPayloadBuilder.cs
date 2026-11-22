using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services.Llm;

internal static class LlmPayloadBuilder
{
    internal static Dictionary<string, object?> Build(
        AgentChatRequest request,
        AgentCachePlan? cache = null,
        AgentReasoningOptions? reasoning = null,
        int? maxOutputTokens = null,
        bool omitMaxOutputTokens = false)
    {
        var messages = request.Messages.Select(BuildMessage).ToList();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = messages,
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

        if (omitMaxOutputTokens)
        {
            // The provider rejected the budget parameter: send no max_tokens.
        }
        else if (maxOutputTokens.HasValue)
            payload["max_tokens"] = maxOutputTokens.Value;
        else if (request.MaxOutputTokens.HasValue)
            payload["max_tokens"] = request.MaxOutputTokens.Value;

        if (reasoning is not null)
        {
            if (reasoning.Format == AgentReasoningFormat.ReasoningEffort)
                payload["reasoning_effort"] = reasoning.Effort;
            else if (reasoning.Format == AgentReasoningFormat.ReasoningObject)
                payload["reasoning"] = new Dictionary<string, object?> { ["effort"] = reasoning.Effort };
        }

        if (cache is not null)
        {
            if (!string.IsNullOrEmpty(cache.PromptCacheKey))
                payload["prompt_cache_key"] = cache.PromptCacheKey;

            foreach (var option in cache.ProviderOptions)
                payload[option.Key] = option.Value;

            if (cache.UseAnthropicBreakpoints)
                ApplyCacheBreakpoints(messages);
        }

        if (request.ProviderOptions is not null)
        {
            foreach (var option in request.ProviderOptions)
                payload[option.Key] = option.Value;
        }

        return payload;
    }

    /// <summary>
    /// Anthropic-style explicit breakpoints (P.5): tools render before system, so
    /// the marker on the first (static) system block caches tools + system, and
    /// the marker on the persisted session baseline extends it. Never mark the
    /// turn context or the ephemeral status: they change every turn. Two markers
    /// stay well under the provider limit of four.
    /// </summary>
    private static void ApplyCacheBreakpoints(List<object> messages)
    {
        var marked = 0;
        for (var index = 0; index < messages.Count && marked < 2; index++)
        {
            if (messages[index] is not Dictionary<string, object?> message)
                continue;
            if (!string.Equals(message.TryGetValue("role", out var role) ? role as string : null,
                    "system", StringComparison.Ordinal))
            {
                continue;
            }

            var text = message.TryGetValue("content", out var content) ? content as string : null;
            if (string.IsNullOrEmpty(text))
                continue;

            var isStatic = index == 0;
            var isSessionContext = text!.StartsWith("<session_context>", StringComparison.Ordinal);
            if (!isStatic && !isSessionContext)
                continue;

            message["content"] = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = text,
                    ["cache_control"] = new Dictionary<string, object?> { ["type"] = "ephemeral" }
                }
            };
            marked++;
        }
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