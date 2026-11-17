using System.Globalization;
using System.Text;
using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services.Llm;

internal sealed class LlmResponseAccumulator
{
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly Dictionary<int, ToolCallBuilder> _toolCalls = new();
    private string? _finishReason;
    private AgentUsage _usage = AgentUsage.Empty;

    public void Append(string json, List<LlmStreamEvent> output)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            _usage = ParseUsage(usage);

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                AppendDelta(delta, output);

            if (choice.TryGetProperty("finish_reason", out var finishReason)
                && finishReason.ValueKind == JsonValueKind.String)
            {
                _finishReason = finishReason.GetString();
            }
        }
    }

    public AgentChatResponse Build(string provider, string model)
    {
        var calls = _toolCalls
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Build(pair.Key))
            .Where(call => !string.IsNullOrWhiteSpace(call.Name))
            .ToList();

        var message = new AgentMessage
        {
            Role = AgentRole.Assistant,
            Content = _content.Length == 0 ? null : _content.ToString(),
            ReasoningContent = _reasoning.Length == 0 ? null : _reasoning.ToString(),
            ToolCalls = calls
        };

        return new AgentChatResponse
        {
            Message = message,
            Usage = _usage,
            FinishReason = _finishReason,
            Model = model,
            Provider = provider
        };
    }

    private void AppendDelta(JsonElement delta, List<LlmStreamEvent> output)
    {
        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                _content.Append(text);
                output.Add(LlmStreamEvent.TextDelta(text!));
            }
        }

        if (TryGetString(delta, "reasoning_content", out var reasoning)
            || TryGetString(delta, "reasoning", out reasoning))
        {
            if (!string.IsNullOrEmpty(reasoning))
            {
                _reasoning.Append(reasoning);
                output.Add(LlmStreamEvent.ReasoningDelta(reasoning!));
            }
        }

        if (delta.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var toolCall in toolCalls.EnumerateArray())
                AppendToolCall(toolCall, output);
        }
    }

    private void AppendToolCall(JsonElement toolCall, List<LlmStreamEvent> output)
    {
        var index = 0;
        if (toolCall.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number)
            index = indexElement.GetInt32();

        if (!_toolCalls.TryGetValue(index, out var builder))
        {
            builder = new ToolCallBuilder();
            _toolCalls[index] = builder;
        }

        if (TryGetString(toolCall, "id", out var id) && !string.IsNullOrEmpty(id))
            builder.Id = id;

        if (toolCall.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(function, "name", out var name) && !string.IsNullOrEmpty(name))
            {
                builder.Name = name;
                if (!builder.EmittedStart)
                {
                    builder.EmittedStart = true;
                    output.Add(LlmStreamEvent.ToolCallStarted(builder.Id ?? "call_" + index, name));
                }
            }

            if (TryGetString(function, "arguments", out var arguments) && arguments.Length > 0)
                builder.AppendArguments(arguments);
        }
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        if (element.TryGetProperty(property, out var propertyElement)
            && propertyElement.ValueKind == JsonValueKind.String)
        {
            value = propertyElement.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static AgentUsage ParseUsage(JsonElement usage)
    {
        var reasoning = 0;
        if (usage.TryGetProperty("completion_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("reasoning_tokens", out var reasoningTokens)
            && reasoningTokens.ValueKind == JsonValueKind.Number)
        {
            reasoning = reasoningTokens.GetInt32();
        }

        decimal? cost = null;
        if (usage.TryGetProperty("cost", out var costElement))
        {
            if (costElement.ValueKind == JsonValueKind.Number)
            {
                cost = (decimal)costElement.GetDouble();
            }
            else if (costElement.ValueKind == JsonValueKind.String
                     && decimal.TryParse(costElement.GetString(), NumberStyles.Float,
                         CultureInfo.InvariantCulture, out var parsedCost))
            {
                cost = parsedCost;
            }
        }

        return new AgentUsage
        {
            PromptTokens = GetInt32(usage, "prompt_tokens"),
            CompletionTokens = GetInt32(usage, "completion_tokens"),
            TotalTokens = GetInt32(usage, "total_tokens"),
            ReasoningTokens = reasoning,
            CostUsd = cost
        };
    }

    private static int GetInt32(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
    }

    private sealed class ToolCallBuilder
    {
        private readonly StringBuilder _arguments = new();

        public string? Id { get; set; }

        public string? Name { get; set; }

        public bool EmittedStart { get; set; }

        public void AppendArguments(string delta)
        {
            var current = _arguments.ToString();
            if (delta.Length >= current.Length
                && delta.StartsWith(current, StringComparison.Ordinal))
            {
                _arguments.Clear();
                _arguments.Append(delta);
                return;
            }

            _arguments.Append(delta);
        }

        public AgentToolCall Build(int index)
        {
            return new AgentToolCall
            {
                Id = string.IsNullOrEmpty(Id) ? "call_" + index.ToString(CultureInfo.InvariantCulture) : Id!,
                Name = Name ?? string.Empty,
                ArgumentsJson = _arguments.Length == 0 ? "{}" : _arguments.ToString()
            };
        }
    }
}