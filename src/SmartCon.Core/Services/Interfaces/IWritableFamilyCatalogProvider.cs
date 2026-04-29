using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Write access to the family catalog.
/// </summary>
public interface IWritableFamilyCatalogProvider
{
    /// <summary>Add a new catalog item with associated file record and version.</summary>
    Task<FamilyImportResult> ImportAsync(FamilyImportRequest request, CancellationToken ct = default);

    /// <summary>Import multiple files from a folder with progress reporting.</summary>
    Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default);

    /// <summary>Update catalog item metadata (name, description, category, tags, status).</summary>
    Task<FamilyCatalogItem> UpdateItemAsync(string id, string? name, string? description, string? category, IReadOnlyList<string>? tags, FamilyContentStatus? status, CancellationToken ct = default);

    /// <summary>Delete a catalog item and its associated data.</summary>
    Task<bool> DeleteItemAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Switch storage mode for a catalog item's current version.
    /// Cached→Linked: removes cached file, keeps original_path.
    /// Linked→Cached: copies file from original_path to cache.
    /// Pass originalPath to update the original path (e.g. when user picks a new location).
    /// </summary>
    Task<bool> SwitchStorageModeAsync(string catalogItemId, FamilyFileStorageMode newMode, string? originalPath = null, CancellationToken ct = default);
}
