namespace SmartCon.Core.Models.FamilyManager;

public sealed record EffectiveCategoryAttribute(
    string AttributeId,
    string Name,
    string? Group,
    int SortOrder,
    bool IsEnabled,
    bool IsInherited,
    string? SourceCategoryId);
