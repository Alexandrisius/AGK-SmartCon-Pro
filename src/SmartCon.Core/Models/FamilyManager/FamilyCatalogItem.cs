namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Logical catalog item representing a Revit family.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="ProviderId">Provider that owns this item.</param>
/// <param name="Name">Display name of the family.</param>
/// <param name="NormalizedName">Lowercase normalized name for search.</param>
/// <param name="Description">User-provided description.</param>
/// <param name="CategoryName">Revit category name.</param>
/// <param name="Manufacturer">Manufacturer name.</param>
/// <param name="Status">Content status.</param>
/// <param name="CurrentVersionId">ID of the current version.</param>
/// <param name="Tags">User-assigned tags.</param>
/// <param name="CreatedAtUtc">Creation timestamp.</param>
/// <param name="UpdatedAtUtc">Last update timestamp.</param>
public sealed record FamilyCatalogItem(
    string Id,
    string ProviderId,
    string Name,
    string NormalizedName,
    string? Description,
    string? CategoryName,
    string? Manufacturer,
    FamilyContentStatus Status,
    string? CurrentVersionId,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
