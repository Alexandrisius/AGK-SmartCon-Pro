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

    /// <summary>
    /// #249 (Phase 5): register an asset row pointing at an EXISTING file
    /// in the shared CAS preview pool — no copy, no move (pool files are
    /// immutable and shared across versions/families). The row is what
    /// ties the version to the pooled content; refcount rules apply at
    /// deletion time.
    /// </summary>
    Task<FamilyAsset> RegisterPooledAssetAsync(string catalogItemId, string? versionLabel, FamilyAssetType assetType, string pooledRelativePath, string? description, CancellationToken ct = default);

    /// <summary>Get all assets for a catalog item, optionally filtered by version.</summary>
    Task<IReadOnlyList<FamilyAsset>> GetAssetsAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);

    /// <summary>Delete an asset (removes record from DB and file from managed storage).</summary>
    Task<bool> DeleteAssetAsync(string assetId, CancellationToken ct = default);

    /// <summary>Resolve absolute path for an asset file.</summary>
    Task<string?> ResolveAssetPathAsync(string assetId, CancellationToken ct = default);

    /// <summary>
    /// Mark an asset as the primary asset for its type (unmarks any previous primary).
    /// Also deletes the derived avatar file (avatar.png, see ADR-047) because it is
    /// rendered from the previous primary image and becomes stale.
    /// </summary>
    Task SetPrimaryAssetAsync(string assetId, CancellationToken ct = default);

    /// <summary>Get the primary image asset for a catalog item, if one is set.</summary>
    Task<FamilyAsset?> GetPrimaryImageAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);

    /// <summary>
    /// Save the cropped avatar thumbnail produced by IAvatarCropService as the catalog
    /// item's derived avatar file ({family-dir}/avatar.png). Overwrites the existing file.
    /// See ADR-047 / issue #131.
    /// </summary>
    Task SaveAvatarAsync(string catalogItemId, string sourcePngPath, CancellationToken ct = default);

    /// <summary>
    /// Resolve the avatar image to display: the derived avatar.png if present,
    /// otherwise the primary image asset. Returns null when neither exists.
    /// Single resolution chain shared by the properties view and the tooltip.
    /// </summary>
    Task<string?> GetAvatarImagePathAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);

    /// <summary>
    /// Remove the derived avatar file and clear the primary flag from all image assets
    /// of the catalog item. Source image assets themselves are kept.
    /// </summary>
    Task ClearAvatarAsync(string catalogItemId, CancellationToken ct = default);

    /// <summary>
    /// Move an asset between shared (versionLabel = null) and per-version storage.
    /// Physically relocates the file and updates the DB record.
    /// </summary>
    /// <param name="assetId">Asset to move.</param>
    /// <param name="newVersionLabel">Target version label, or null to make it shared across all versions.</param>
    Task SetAssetVersionBindingAsync(string assetId, string? newVersionLabel, CancellationToken ct = default);
}
