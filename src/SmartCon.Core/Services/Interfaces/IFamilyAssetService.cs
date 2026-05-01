using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Manages auxiliary assets (images, videos, documents, 3D models, lookup tables)
/// attached to family catalog items.
/// </summary>
public interface IFamilyAssetService
{
    /// <summary>Add an asset file to a catalog item. File is copied into managed storage.</summary>
    Task<FamilyAsset> AddAssetAsync(string catalogItemId, string? versionLabel, FamilyAssetType assetType, string sourceFilePath, string? description, CancellationToken ct = default);

    /// <summary>Get all assets for a catalog item, optionally filtered by version.</summary>
    Task<IReadOnlyList<FamilyAsset>> GetAssetsAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);

    /// <summary>Delete an asset (removes record from DB and file from managed storage).</summary>
    Task<bool> DeleteAssetAsync(string assetId, CancellationToken ct = default);

    /// <summary>Resolve absolute path for an asset file.</summary>
    Task<string?> ResolveAssetPathAsync(string assetId, CancellationToken ct = default);

    /// <summary>Mark an asset as the primary asset for its type (unmarks any previous primary).</summary>
    Task SetPrimaryAssetAsync(string assetId, CancellationToken ct = default);

    /// <summary>Get the primary image asset for a catalog item, if one is set.</summary>
    Task<FamilyAsset?> GetPrimaryImageAsync(string catalogItemId, CancellationToken ct = default);
}
