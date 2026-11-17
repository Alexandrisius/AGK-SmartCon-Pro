namespace SmartCon.Core.Models.Agent;

/// <summary>One executed tool call, persisted for friction analysis (Phase 3).</summary>
public sealed class AgentToolCallRecord
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    public required string ToolName { get; init; }

    public string? ArgumentsJson { get; init; }

    public string? ResultJson { get; init; }

    /// <summary>ok | error | cancelled</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    public long DurationMs { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Feedback the model reports about its own tooling (mvp-plan Phase 3):
/// written by the <c>report_tooling_feedback</c> meta-tool.
/// </summary>
public sealed class AgentFeedbackEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string? SessionId { get; init; }

    public string? DocumentId { get; init; }

    public string? Model { get; init; }

    public string? Provider { get; init; }

    public string? ToolName { get; init; }

    /// <summary>tools | prompt | ui</summary>
    public required string Area { get; init; }

    public int Rating { get; init; }

    public required string Issue { get; init; }

    public string? Suggestion { get; init; }

    public string? ContextJson { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
