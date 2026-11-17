namespace SmartCon.Core.Models.Agent;

public sealed class AgentConversationHeader
{
    public Guid Id { get; init; }

    public required string Title { get; init; }

    public required string DocumentId { get; init; }

    public string? DocumentPath { get; init; }

    public string? RevitVersion { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public int TokenCount { get; init; }
}