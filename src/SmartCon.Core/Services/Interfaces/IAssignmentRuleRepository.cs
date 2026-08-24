using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Persistence contract for auto-assignment rules (#241): OR-groups per
/// catalog category with AND-conditions inside each group. Implemented by
/// the local SQLite catalog (LocalAssignmentRuleRepository).
/// </summary>
public interface IAssignmentRuleRepository
{
    /// <summary>All rule groups with their conditions (both enabled and
    /// disabled — the caller filters), ordered by category id and group
    /// sort order.</summary>
    Task<IReadOnlyList<AssignmentRuleGroup>> GetGroupsWithConditionsAsync(CancellationToken ct = default);

    /// <summary>All rule groups (with conditions) of one category.</summary>
    Task<IReadOnlyList<AssignmentRuleGroup>> GetGroupsForCategoryAsync(string categoryId, CancellationToken ct = default);

    /// <summary>Number of enabled groups per category id — for the tree
    /// indicator. Categories without groups are absent.</summary>
    Task<IReadOnlyDictionary<string, int>> GetEnabledGroupCountsAsync(CancellationToken ct = default);

    /// <summary>Creates an empty rule group. Returns the created group
    /// (with a new id and the next sort order of the category).</summary>
    Task<AssignmentRuleGroup> CreateGroupAsync(string categoryId, CancellationToken ct = default);

    /// <summary>Partial update: only the provided fields are written
    /// (<c>null</c> = keep the stored value).</summary>
    Task<bool> UpdateGroupAsync(string groupId, int? sortOrder, bool? isEnabled, CancellationToken ct = default);

    /// <summary>Deletes the group with its conditions (FK CASCADE).
    /// Returns <c>false</c> when the group does not exist.</summary>
    Task<bool> DeleteGroupAsync(string groupId, CancellationToken ct = default);

    /// <summary>Creates a condition inside the group. Returns the created
    /// condition (with a new id and the next sort order of the group).</summary>
    Task<AssignmentCondition> CreateConditionAsync(
        string groupId,
        AssignmentConditionSourceKind sourceKind,
        string? attributeId,
        AssignmentSystemField? systemField,
        ValidationRuleOperator op,
        string? valueText,
        double? valueNumber,
        double? minValue,
        double? maxValue,
        bool isEnabled,
        CancellationToken ct = default);

    /// <summary>Full-shape update of one condition (the editor saves the
    /// whole row — a partial update cannot distinguish "keep" from "set
    /// NULL" for attribute_id/system_key). Returns <c>false</c> when the
    /// condition does not exist.</summary>
    Task<bool> UpdateConditionAsync(AssignmentCondition condition, CancellationToken ct = default);

    /// <summary>Deletes one condition. Returns <c>false</c> when the
    /// condition does not exist.</summary>
    Task<bool> DeleteConditionAsync(string conditionId, CancellationToken ct = default);
}
