namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Search query for the family catalog.
/// </summary>
/// <param name="SearchText">Free-text search across name and description.</param>
/// <param name="CategoryFilter">Filter by category name.</param>
/// <param name="StatusFilter">Filter by content status.</param>
/// <param name="Tags">Filter by tags (all must match).</param>
/// <param name="ManufacturerFilter">Filter by manufacturer.</param>
/// <param name="Sort">Sort order.</param>
/// <param name="Offset">Pagination offset.</param>
/// <param name="Limit">Maximum items to return.</param>
public sealed record FamilyCatalogQuery(
    string? SearchText,
    string? CategoryFilter,
    FamilyContentStatus? StatusFilter,
    IReadOnlyList<string>? Tags,
    string? ManufacturerFilter,
    FamilyCatalogSort Sort,
    int Offset,
    int Limit);
