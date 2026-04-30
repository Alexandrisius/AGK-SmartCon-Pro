namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Auxiliary asset (image, video, document, 3D model, lookup table) attached to a family.
/// Files are stored in managed storage: {db-root}/files/{family-id}/{version}/assets/{type}/.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="CatalogItemId">Parent catalog item ID.</param>
/// <param name="VersionLabel">Version label this asset belongs to (null = applies to all versions).</param>
/// <param name="AssetType">Type of asset.</param>
/// <param name="FileName">Original file name with extension.</param>
/// <param name="RelativePath">Relative path within managed storage.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Description">User-provided description.</param>
/// <param name="CreatedAtUtc">When the asset was added.</param>
public sealed record FamilyAsset(
    string Id,
    string CatalogItemId,
    string? VersionLabel,
    FamilyAssetType AssetType,
    string FileName,
    string RelativePath,
    long SizeBytes,
    string? Description,
    DateTimeOffset CreatedAtUtc);
