using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

public enum AgentProgressKind
{
    Status = 0,
    TextDelta = 1,
    ReasoningDelta = 2,
    ToolStarted = 3,
    ToolCompleted = 4,
    TurnCompleted = 5,
    Error = 6
}

public sealed class AgentProgress
{
    public AgentProgressKind Kind { get; init; }

    public string? Text { get; init; }

    public string? Details { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public bool Success { get; init; }

    public string? Error { get; init; }

    public AgentUsage? Usage { get; init; }

    public int ToolCallCount { get; init; }

    public static AgentProgress Status(string text) =>
        new() { Kind = AgentProgressKind.Status, Text = text };

    public static AgentProgress TextDelta(string text) =>
        new() { Kind = AgentProgressKind.TextDelta, Text = text };

    public static AgentProgress ReasoningDelta(string text) =>
        new() { Kind = AgentProgressKind.ReasoningDelta, Text = text };
}