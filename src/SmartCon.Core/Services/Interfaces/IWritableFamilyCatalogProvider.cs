using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Write access to the family catalog.
/// All imports copy files into managed storage at {db-root}/files/.
/// </summary>
public interface IWritableFamilyCatalogProvider
{
    /// <summary>Import a single family file into the published catalog.</summary>
    Task<FamilyImportResult> ImportAsync(FamilyImportRequest request, CancellationToken ct = default);

    /// <summary>Import multiple files from a folder with progress reporting.</summary>
    Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default);

    /// <summary>Update catalog item metadata (name, description, category, tags, status).</summary>
    Task<FamilyCatalogItem> UpdateItemAsync(string id, string? name, string? description, string? category, IReadOnlyList<string>? tags, ContentStatus? status, CancellationToken ct = default);

    /// <summary>Delete a catalog item, its versions, files from managed storage, and assets.</summary>
    Task<bool> DeleteItemAsync(string id, CancellationToken ct = default);
}
