namespace SmartCon.Core.Models.FamilyManager;

public sealed record FamilyCatalogQuery(
    string? SearchText,
    string? CategoryFilter,
    ContentStatus? StatusFilter,
    IReadOnlyList<string>? Tags,
    FamilyCatalogSort Sort,
    int Offset,
    int Limit,
    bool IncludeUncategorized = false,
    IReadOnlyList<string>? CategoryIdsFilter = null,
    bool ExcludeUncategorized = false,
    IReadOnlyList<AttributeFilterCondition>? AttributeFilters = null);
