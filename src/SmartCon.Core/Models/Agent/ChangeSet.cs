namespace SmartCon.Core.Models.Agent;

public enum ChangeStatus
{
    Pending = 0,
    Applied = 1,
    Failed = 2,
    Skipped = 3,
    RolledBack = 4
}

public sealed class ChangeSetHeader
{
    public Guid Id { get; init; }

    public Guid? ConversationId { get; init; }

    public string? ToolCallId { get; init; }

    public required string Operation { get; init; }

    public required string DocumentId { get; init; }

    public string? DocumentPath { get; init; }

    public string? RevitVersion { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime? AppliedAt { get; init; }
}

public sealed class ChangeItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid ChangeSetId { get; init; }

    public string? UniqueId { get; init; }

    public long? ElementId { get; init; }

    public string? ParameterName { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public ChangeStatus Status { get; init; }

    public string? Error { get; init; }
}

public sealed class ChangeSetDetails
{
    public required ChangeSetHeader Header { get; init; }

    public IReadOnlyList<ChangeItem> Items { get; init; } = Array.Empty<ChangeItem>();
}

public sealed class WriteOptions
{
    public bool DryRun { get; init; } = true;

    public bool Atomic { get; init; }

    public bool RequireConfirm { get; init; } = true;

    public int MaxElements { get; init; } = 500;

    public string? ExpectedDocumentId { get; init; }
}