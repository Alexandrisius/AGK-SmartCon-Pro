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

    /// <summary>Routing category (Read / Write / Meta) used by the tool router.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// Russian and English synonyms used by <see cref="Services.Agent.RuleToolRouter"/>.
    /// Empty tags mean the tool is reachable only through the core set or find_tools.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>True when the tool accepts batches of many items in one call.</summary>
    public bool SupportsBulk { get; init; }
}