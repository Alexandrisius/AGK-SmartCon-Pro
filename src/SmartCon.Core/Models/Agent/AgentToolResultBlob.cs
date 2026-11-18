namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Full redacted tool result kept outside the conversation after the model got a
/// trimmed preview (runbook H1-14). The handle is the tool-call id, so the model
/// can fetch the rest with <c>read_tool_result</c> even after a restart.
/// </summary>
public sealed class AgentToolResultBlob
{
    public required string ToolCallId { get; init; }

    public required Guid SessionId { get; init; }

    public required string ResultJson { get; init; }

    public DateTime CreatedAt { get; init; }
}
