namespace SmartCon.Core.Models.Agent;

public sealed class AgentAuditEntry
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public string? Actor { get; init; }

    public string? DocumentPath { get; init; }

    public required string Tool { get; init; }

    public required string ArgsHash { get; init; }

    public required string Result { get; init; }

    public int AffectedCount { get; init; }

    public long DurationMs { get; init; }

    public string? ErrorCode { get; init; }
}