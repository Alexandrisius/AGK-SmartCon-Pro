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

    /// <summary>Catalog pack name (see <see cref="SmartCon.Core.Services.Agent.AgentToolPacks"/>).</summary>
    public string Pack { get; init; } = string.Empty;

    /// <summary>One-line statement of what the tool does.</summary>
    public string Purpose { get; init; } = string.Empty;

    public string WhenToUse { get; init; } = string.Empty;

    public string WhenNotToUse { get; init; } = string.Empty;

    public IReadOnlyList<string> Examples { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Synonyms { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NegativeExamples { get; init; } = Array.Empty<string>();

    public bool Idempotent { get; init; }

    public int TimeoutMs { get; init; } = 30000;

    /// <summary>"active_view", "selection" or null when the tool has no context requirement.</summary>
    public string? RequiredContext { get; init; }

    /// <summary>Alias of <see cref="SupportsBulk"/> (runbook §7.1 contract).</summary>
    public bool Bulk
    {
        get => SupportsBulk;
        init => SupportsBulk = value;
    }
}