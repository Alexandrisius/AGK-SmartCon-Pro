namespace SmartCon.Core.Models.Agent;

public sealed class AgentChatRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<AgentMessage> Messages { get; init; }

    public IReadOnlyList<AgentToolDefinition> Tools { get; init; } = Array.Empty<AgentToolDefinition>();

    public double? Temperature { get; init; }

    public int? MaxOutputTokens { get; init; }
}

public sealed class AgentUsage
{
    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public int ReasoningTokens { get; init; }

    public int CachedTokens { get; init; }

    public int CacheWriteTokens { get; init; }

    public int TotalTokens { get; init; }

    public decimal? CostUsd { get; init; }

    public static AgentUsage Empty { get; } = new();
}

public sealed class AgentChatResponse
{
    public required AgentMessage Message { get; init; }

    public AgentUsage Usage { get; init; } = AgentUsage.Empty;

    public string? FinishReason { get; init; }

    public string? Model { get; init; }

    public string? Provider { get; init; }

    public string? RequestId { get; init; }
}

public enum LlmStreamEventKind
{
    TextDelta = 0,
    ReasoningDelta = 1,
    ToolCallStarted = 2,
    Usage = 3,
    Completed = 4,
    Error = 5
}

public sealed class LlmStreamEvent
{
    public LlmStreamEventKind Kind { get; init; }

    public string? Text { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public AgentUsage? Usage { get; init; }

    public AgentChatResponse? Completion { get; init; }

    public string? Error { get; init; }

    public static LlmStreamEvent TextDelta(string text) =>
        new() { Kind = LlmStreamEventKind.TextDelta, Text = text };

    public static LlmStreamEvent ReasoningDelta(string text) =>
        new() { Kind = LlmStreamEventKind.ReasoningDelta, Text = text };

    public static LlmStreamEvent ToolCallStarted(string toolCallId, string toolName) =>
        new() { Kind = LlmStreamEventKind.ToolCallStarted, ToolCallId = toolCallId, ToolName = toolName };

    public static LlmStreamEvent UsageReceived(AgentUsage usage) =>
        new() { Kind = LlmStreamEventKind.Usage, Usage = usage };

    public static LlmStreamEvent Completed(AgentChatResponse response) =>
        new() { Kind = LlmStreamEventKind.Completed, Completion = response };

    public static LlmStreamEvent Failed(string error) =>
        new() { Kind = LlmStreamEventKind.Error, Error = error };
}