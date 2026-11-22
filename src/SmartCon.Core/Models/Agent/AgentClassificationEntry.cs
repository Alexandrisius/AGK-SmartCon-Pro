namespace SmartCon.Core.Models.Agent;

/// <summary>
/// One row of a classification table: the code, its description and its place
/// in the tree. <see cref="Breadcrumb"/> and <see cref="IsLeaf"/> are derived by
/// <c>AgentClassificationTable.Build</c> and are empty there.
/// </summary>
public sealed record AgentClassificationEntry
{
    public required string Code { get; init; }

    public string Description { get; init; } = string.Empty;

    public int Level { get; init; }

    public string? ParentCode { get; init; }

    /// <summary>Descriptions from the root to this row, «раздел › подраздел › элемент».</summary>
    public string Breadcrumb { get; init; } = string.Empty;

    public long? CategoryId { get; init; }

    /// <summary>True when no other row names this one as its parent.</summary>
    public bool IsLeaf { get; init; }
}
