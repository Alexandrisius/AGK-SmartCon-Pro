namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Retrieval metadata for one agent tool (runbook §7.1, H1-06). Cards are plain
/// data: they are indexed by name and copied onto <see cref="AgentToolDefinition"/>
/// by <see cref="SmartCon.Core.Services.Agent.AgentToolCatalog.Enrich"/>.
/// </summary>
public sealed class AgentToolCard
{
    public required string ToolName { get; init; }

    public required string Pack { get; init; }

    public required string Purpose { get; init; }

    public required string WhenToUse { get; init; }

    public required string WhenNotToUse { get; init; }

    public IReadOnlyList<string> Examples { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Synonyms { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NegativeExamples { get; init; } = Array.Empty<string>();

    public bool Idempotent { get; init; }

    public int TimeoutMs { get; init; } = 30000;

    public string? RequiredContext { get; init; }

    public bool Bulk { get; init; }

    public string? BatchAnalogue { get; init; }
}
