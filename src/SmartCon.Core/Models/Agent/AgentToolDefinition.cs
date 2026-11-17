namespace SmartCon.Core.Models.Agent;

public enum AgentToolRisk
{
    Read = 0,
    Write = 1,
    Destructive = 2
}

public sealed class AgentToolDefinition
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public AgentToolRisk Risk { get; init; }

    public bool SupportsDryRun { get; init; }

    public required string InputSchemaJson { get; init; }
}