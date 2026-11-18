namespace SmartCon.Core.Models.Agent;

/// <summary>Persisted checkpoint of a compacted conversation (runbook H1-16).</summary>
public sealed class AgentCompactionRecord
{
    public required string Id { get; init; }

    public required Guid SessionId { get; init; }

    /// <summary>1-based ordinal of the first covered message in the session.</summary>
    public int FromRowId { get; init; } = 1;

    /// <summary>1-based ordinal of the last covered message in the session.</summary>
    public required int ToRowId { get; init; }

    public required string Summary { get; init; }

    public int TokensBefore { get; init; }

    public int TokensAfter { get; init; }

    public string SourcePrefixHash { get; init; } = string.Empty;

    public string? Model { get; init; }

    public string? AckMessageId { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
