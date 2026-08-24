namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One OR-group of auto-assignment rules (#241) attached to a catalog
/// category: the family matches the category when AT LEAST ONE enabled
/// group of that category has ALL its enabled conditions satisfied
/// (OR of AND-groups). A group with no conditions matches nothing.
/// </summary>
public sealed record AssignmentRuleGroup(
    string Id,
    string CategoryId,
    int SortOrder,
    bool IsEnabled,
    IReadOnlyList<AssignmentCondition> Conditions);
