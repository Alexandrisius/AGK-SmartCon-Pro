namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Logical catalog item representing a published Revit family.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="Name">Display name of the family.</param>
/// <param name="NormalizedName">Lowercase normalized name for search.</param>
/// <param name="Description">User-provided description.</param>
/// <param name="CategoryName">Revit category name.</param>
/// <param name="Manufacturer">Manufacturer name.</param>
/// <param name="ContentStatus">Publication status (Active/Deprecated/Retired).</param>
/// <param name="CurrentVersionLabel">Label of the current published version (e.g. "v1").</param>
/// <param name="Tags">User-assigned tags.</param>
/// <param name="PublishedBy">User who published this family.</param>
/// <param name="CreatedAtUtc">Creation timestamp.</param>
/// <param name="UpdatedAtUtc">Last update timestamp.</param>
public sealed record FamilyCatalogItem(
    string Id,
    string Name,
    string NormalizedName,
    string? Description,
    string? CategoryName,
    string? Manufacturer,
    ContentStatus ContentStatus,
    string? CurrentVersionLabel,
    IReadOnlyList<string> Tags,
    string? PublishedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
