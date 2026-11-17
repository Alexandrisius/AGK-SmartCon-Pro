namespace SmartCon.Core.Models.Agent;

public enum AgentConfirmationResult
{
    Approved = 0,
    Rejected = 1,
    TimedOut = 2,
    Cancelled = 3
}

/// <summary>
/// Request shown to the user before a write tool changes the model.
/// The digest binds the approval to the exact canonical action, so a
/// changed argument set requires a new approval (mvp-plan §9.8 L3).
/// </summary>
public sealed class AgentConfirmationRequest
{
    public required string ToolName { get; init; }

    public required string Operation { get; init; }

    public required string Summary { get; init; }

    public required string Digest { get; init; }

    public required string ArgumentsJson { get; init; }

    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();

    public int AffectedCount { get; init; }

    public bool Reversible { get; init; } = true;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}
