namespace SmartCon.Core.Models.Agent;

public enum AgentRole
{
    System = 0,
    User = 1,
    Assistant = 2,
    Tool = 3
}

public sealed class AgentToolCall
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string ArgumentsJson { get; init; }
}

public sealed class AgentAttachment
{
    public required string FileName { get; init; }

    public required string MimeType { get; init; }

    public required string Base64Data { get; init; }
}

public sealed class AgentMessage
{
    public AgentRole Role { get; init; }

    public string? Content { get; init; }

    public string? ReasoningContent { get; init; }

    public IReadOnlyList<AgentToolCall> ToolCalls { get; init; } = Array.Empty<AgentToolCall>();

    public string? ToolCallId { get; init; }

    public IReadOnlyList<AgentAttachment> Attachments { get; init; } = Array.Empty<AgentAttachment>();

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public int TokenCount { get; init; }

    public static AgentMessage System(string content) => new() { Role = AgentRole.System, Content = content };

    public static AgentMessage User(string content, IReadOnlyList<AgentAttachment>? attachments = null) =>
        new()
        {
            Role = AgentRole.User,
            Content = content,
            Attachments = attachments ?? Array.Empty<AgentAttachment>()
        };

    public static AgentMessage Assistant(
        string? content,
        IReadOnlyList<AgentToolCall>? toolCalls = null,
        string? reasoningContent = null) =>
        new()
        {
            Role = AgentRole.Assistant,
            Content = content,
            ToolCalls = toolCalls ?? Array.Empty<AgentToolCall>(),
            ReasoningContent = reasoningContent
        };

    public static AgentMessage ToolResult(string toolCallId, string content) =>
        new() { Role = AgentRole.Tool, ToolCallId = toolCallId, Content = content };
}