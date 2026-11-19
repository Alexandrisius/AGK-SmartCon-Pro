namespace SmartCon.Core.Models.Agent;

public sealed class AgentProjectContext
{
    public bool HasDocument { get; init; } = true;

    public required string DocumentId { get; init; }

    public required string Title { get; init; }

    public string? PathName { get; init; }

    public required string RevitVersion { get; init; }

    public string? Units { get; init; }

    public bool IsWorkshared { get; init; }

    public bool IsModified { get; init; }

    public long? ActiveViewId { get; init; }

    public string? ActiveViewName { get; init; }

    public string? ActiveViewType { get; init; }

    public int LevelCount { get; init; }

    public int ViewCount { get; init; }

    public int SheetCount { get; init; }

    public IReadOnlyList<string> TopCategories { get; init; } = Array.Empty<string>();
}