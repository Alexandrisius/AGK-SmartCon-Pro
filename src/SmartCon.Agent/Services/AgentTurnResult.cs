using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services;

public sealed class AgentTurnResult
{
    public required string FinalText { get; init; }

    public int ToolCallCount { get; init; }

    public int Rounds { get; init; }

    public AgentUsage Usage { get; init; } = AgentUsage.Empty;

    public bool MaxRoundsReached { get; init; }
}