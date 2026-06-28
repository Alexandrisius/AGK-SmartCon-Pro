namespace SmartCon.Core.Models.FamilyManager;

public sealed record FamilyCatalogItem(
    string Id,
    string Name,
    string NormalizedName,
    string? Description,
    string? CategoryPath,
    string? CategoryId,
    string? Manufacturer,
    ContentStatus ContentStatus,
    string? CurrentVersionLabel,
    IReadOnlyList<string> Tags,
    string? PublishedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string FamilySource = "loadable",
    string? RevitCategory = null,
    string? ContentHash = null,
    int? HashFormatVersion = null);
