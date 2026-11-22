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
    private readonly Dictionary<string, int> _idToIndex = new(StringComparer.Ordinal);
    private int _nextIndex;
    private int _lastIndex = -1;
    private string? _finishReason;
    private string? _requestId;
    private string? _streamError;
    private AgentUsage _usage = AgentUsage.Empty;

    public void Append(string json, List<LlmStreamEvent> output)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("id", out var requestId) && requestId.ValueKind == JsonValueKind.String)
        {
            var value = requestId.GetString();
            if (!string.IsNullOrEmpty(value))
                _requestId = value;
        }

        // Some providers (OpenRouter among them) report a mid-stream failure as
        // HTTP 200 with a top-level "error" object and finish_reason "error".
        // Ignoring it turned a broken answer into an empty successful turn.
        if (root.TryGetProperty("error", out var errorElement))
        {
            var message = ReadErrorMessage(errorElement);
            if (!string.IsNullOrWhiteSpace(message))
                _streamError ??= message;
        }

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
                if (string.Equals(_finishReason, "error", StringComparison.OrdinalIgnoreCase))
                    _streamError ??= "provider reported finish_reason=error";
            }
        }
    }

    public AgentChatResponse Build(string provider, string model)
    {
        if (!string.IsNullOrWhiteSpace(_streamError))
        {
            throw new AgentLlmException(
                "LLM provider reported an error mid-stream: " + _streamError +
                " [Action: retry the turn; if it repeats, switch the model or check the provider status]");
        }

        var named = _toolCalls
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Build(pair.Key))
            .Where(call => !string.IsNullOrWhiteSpace(call.Name))
            .ToList();

        if (_toolCalls.Count > 0 && named.Count == 0
            && _content.Length == 0 && _reasoning.Length == 0)
        {
            throw new AgentLlmException(
                "LLM returned a tool call without a name and no text. The provider " +
                "response is unusable; retry the turn or switch the model.");
        }

        var finishReason = _finishReason;
        var truncated = false;
        if (finishReason is { Length: > 0 }
            && !string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(finishReason, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            if (finishReason.StartsWith("content_filter", StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentLlmException(
                    "The provider blocked the response with a content filter. " +
                    "[Action: rephrase the request or choose another model]");
            }

            // finish_reason "length": the reply (or the reasoning before it) hit
            // the output token budget. The completion is marked truncated; the
            // orchestrator discards it and re-requests the round.
            truncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);
        }

        var message = new AgentMessage
        {
            Role = AgentRole.Assistant,
            Content = _content.Length == 0 ? null : _content.ToString(),
            ReasoningContent = _reasoning.Length == 0 ? null : _reasoning.ToString(),
            ToolCalls = named
        };

        return new AgentChatResponse
        {
            Message = message,
            Usage = _usage,
            FinishReason = _finishReason,
            Truncated = truncated,
            Model = model,
            Provider = provider,
            RequestId = _requestId
        };
    }

    private static string? ReadErrorMessage(JsonElement errorElement)
    {
        switch (errorElement.ValueKind)
        {
            case JsonValueKind.String:
                return errorElement.GetString();

            case JsonValueKind.Object:
                foreach (var property in new[] { "message", "error", "detail", "type", "code" })
                {
                    if (errorElement.TryGetProperty(property, out var nested))
                    {
                        var text = ReadErrorMessage(nested);
                        if (!string.IsNullOrWhiteSpace(text))
                            return text;
                    }
                }

                return Truncate(errorElement.GetRawText(), 400);

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return errorElement.GetRawText();

            default:
                return null;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value.Substring(0, maxLength) + "…";

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
            var callArray = toolCalls.EnumerateArray().ToList();
            for (var position = 0; position < callArray.Count; position++)
                AppendToolCall(callArray[position], position, callArray.Count, output);
        }
    }

    /// <summary>
    /// Some OpenAI-compatible providers omit <c>index</c> entirely and may put
    /// several tool calls into one delta. Treating a missing index as 0 merged
    /// every call into one builder: the last name won and the arguments of all
    /// calls were concatenated, so the executor ran the wrong tool with stale
    /// arguments (stress test #2, critical). Calls are now keyed by provider id
    /// when present, otherwise allocated in order; an id-less delta continues
    /// the previous call.
    /// </summary>
    private void AppendToolCall(
        JsonElement toolCall,
        int position,
        int arrayCount,
        List<LlmStreamEvent> output)
    {
        var hasIndex = toolCall.TryGetProperty("index", out var indexElement)
                       && indexElement.ValueKind == JsonValueKind.Number;
        var id = TryGetString(toolCall, "id", out var parsedId) && !string.IsNullOrEmpty(parsedId)
            ? parsedId
            : null;

        int index;
        if (hasIndex)
        {
            index = indexElement.GetInt32();
            if (id is not null
                && _toolCalls.TryGetValue(index, out var existing)
                && !string.IsNullOrEmpty(existing.Id)
                && !string.Equals(existing.Id, id, StringComparison.Ordinal))
            {
                index = AllocateIndex();
            }
        }
        else if (id is not null)
        {
            if (!_idToIndex.TryGetValue(id, out index))
                index = AllocateIndex();
        }
        else if (arrayCount > 1)
        {
            // Several id-less calls in one delta: each element is a distinct call.
            index = AllocateIndex();
        }
        else
        {
            index = _lastIndex >= 0 ? _lastIndex : AllocateIndex();
        }

        if (id is not null)
            _idToIndex[id] = index;
        _lastIndex = index;

        if (!_toolCalls.TryGetValue(index, out var builder))
        {
            builder = new ToolCallBuilder();
            _toolCalls[index] = builder;
        }

        if (id is not null)
            builder.Id = id;

        if (toolCall.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(function, "name", out var name) && !string.IsNullOrEmpty(name))
            {
                builder.Name = name;
                if (!builder.EmittedStart)
                {
                    builder.EmittedStart = true;
                    output.Add(LlmStreamEvent.ToolCallStarted(builder.EnsureId(index), name));
                }
            }

            if (TryGetString(function, "arguments", out var arguments) && arguments.Length > 0)
                builder.AppendArguments(arguments);
        }
    }

    private int AllocateIndex() => _nextIndex++;

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
        var promptTokens = GetInt32(usage, "prompt_tokens");

        var reasoning = TryGetNestedInt32(
            usage, "completion_tokens_details", "reasoning_tokens", out var parsedReasoning)
            ? parsedReasoning
            : 0;

        var cached = 0;
        if (!TryGetNestedInt32(usage, "prompt_tokens_details", "cached_tokens", out cached)
            && !TryGetInt32(usage, "prompt_cache_hit_tokens", out cached))
        {
            cached = 0;
        }

        if (cached < 0)
            cached = 0;
        if (promptTokens > 0 && cached > promptTokens)
            cached = promptTokens;

        var cacheWrite = 0;
        if (!TryGetNestedInt32(usage, "prompt_tokens_details", "cache_write_tokens", out cacheWrite)
            && !TryGetInt32(usage, "cache_write_tokens", out cacheWrite)
            && !TryGetInt32(usage, "prompt_cache_write_tokens", out cacheWrite))
        {
            cacheWrite = 0;
        }

        if (cacheWrite < 0)
            cacheWrite = 0;
        if (promptTokens > 0 && cacheWrite > promptTokens)
            cacheWrite = promptTokens;

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
            PromptTokens = promptTokens,
            CompletionTokens = GetInt32(usage, "completion_tokens"),
            TotalTokens = GetInt32(usage, "total_tokens"),
            ReasoningTokens = reasoning,
            CachedTokens = cached,
            CacheWriteTokens = cacheWrite,
            CostUsd = cost
        };
    }

    private static int GetInt32(JsonElement element, string property)
    {
        return TryGetInt32(element, property, out var value) ? value : 0;
    }

    private static bool TryGetInt32(JsonElement element, string property, out int value)
    {
        if (element.TryGetProperty(property, out var propertyElement)
            && propertyElement.ValueKind == JsonValueKind.Number)
        {
            value = propertyElement.GetInt32();
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetNestedInt32(
        JsonElement element,
        string container,
        string property,
        out int value)
    {
        value = 0;
        return element.TryGetProperty(container, out var containerElement)
            && containerElement.ValueKind == JsonValueKind.Object
            && TryGetInt32(containerElement, property, out value);
    }

    private sealed class ToolCallBuilder
    {
        private readonly StringBuilder _arguments = new();
        private string? _syntheticId;

        public string? Id { get; set; }

        public string? Name { get; set; }

        public bool EmittedStart { get; set; }

        /// <summary>
        /// Id for providers that never send one. A plain "call_0" collided across
        /// conversations (tool_result_blobs keys on tool_call_id), so the first
        /// stored result was silently overwritten by the next chat's call_0.
        /// </summary>
        public string EnsureId(int index)
        {
            // A synthetic id that was already announced to the UI must stay the
            // call id: the tool result and the stored blob use it too.
            if (!string.IsNullOrEmpty(_syntheticId))
                return _syntheticId!;

            if (!string.IsNullOrEmpty(Id))
                return Id!;

            return _syntheticId = "call_" + index.ToString(CultureInfo.InvariantCulture) +
                                  "_" + Guid.NewGuid().ToString("N");
        }

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
                Id = EnsureId(index),
                Name = Name ?? string.Empty,
                ArgumentsJson = _arguments.Length == 0 ? "{}" : _arguments.ToString()
            };
        }
    }
}