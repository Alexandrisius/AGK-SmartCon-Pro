namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Quarantine snapshot of a parameter view filter taken right before deletion
/// (S.26): rules, categories and the applied views with their overrides, so the
/// filter can be recreated by hand. Stored in agent.db, never in the model.
/// </summary>
public sealed class AgentFilterSnapshot
{
    public required string Id { get; init; }

    public required string FilterName { get; init; }

    /// <summary>UniqueId of the deleted ParameterFilterElement, when it was readable.</summary>
    public string? UniqueId { get; init; }

    /// <summary>Integer element id of the deleted filter.</summary>
    public long? ElementId { get; init; }

    /// <summary>Identity of the document the filter lived in.</summary>
    public string? DocumentId { get; init; }

    /// <summary>Rule tree serialized from the reader (operators and values).</summary>
    public required string RulesJson { get; init; }

    /// <summary>Categories (id + name) the filter was scoped to.</summary>
    public required string CategoriesJson { get; init; }

    /// <summary>Views the filter was applied to, with overrides and checkboxes.</summary>
    public required string AppliedViewsJson { get; init; }

    /// <summary>Reason the snapshot landed in quarantine (delete_filter, merge_duplicate_filters).</summary>
    public string? Reason { get; init; }

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}
