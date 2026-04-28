namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Request to import all .rfa files from a folder.
/// </summary>
/// <param name="FolderPath">Absolute path to the folder.</param>
/// <param name="Recursive">Whether to search subfolders.</param>
/// <param name="Category">User-assigned category for all files.</param>
/// <param name="Tags">User-assigned tags for all files.</param>
/// <param name="Description">User-assigned description prefix for all files.</param>
/// <param name="StorageMode">Storage mode (default: Cached).</param>
public sealed record FamilyFolderImportRequest(
    string FolderPath,
    bool Recursive,
    string? Category,
    IReadOnlyList<string>? Tags,
    string? Description,
    FamilyFileStorageMode StorageMode = FamilyFileStorageMode.Cached);
