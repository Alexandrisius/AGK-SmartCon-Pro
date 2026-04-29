namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Request to import a single family file.
/// </summary>
/// <param name="FilePath">Absolute path to the .rfa file.</param>
/// <param name="Category">User-assigned category.</param>
/// <param name="Tags">User-assigned tags.</param>
/// <param name="Description">User-assigned description.</param>
/// <param name="StorageMode">Storage mode (default: Linked — no duplication).</param>
public sealed record FamilyImportRequest(
    string FilePath,
    string? Category,
    IReadOnlyList<string>? Tags,
    string? Description,
    FamilyFileStorageMode StorageMode = FamilyFileStorageMode.Linked);
