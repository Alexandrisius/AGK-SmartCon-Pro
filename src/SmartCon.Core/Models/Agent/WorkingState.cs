namespace SmartCon.Core.Models.Agent;

public sealed class WorkingState
{
    public string? Goal { get; init; }

    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = Array.Empty<string>();

    public string? DocumentId { get; init; }

    public string? DocumentPath { get; init; }

    public string? RevitVersion { get; init; }

    public string? BuildConfiguration { get; init; }

    public long? ActiveViewId { get; init; }

    public string? ActiveViewName { get; init; }

    public IReadOnlyList<string> SelectedUniqueIds { get; init; } = Array.Empty<string>();

    public Guid? CurrentChangeSetId { get; init; }

    public string? LastBuild { get; init; }

    public string? LastTest { get; init; }

    public IReadOnlyList<string> OpenErrors { get; init; } = Array.Empty<string>();

    public string? PendingDecision { get; init; }

    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}