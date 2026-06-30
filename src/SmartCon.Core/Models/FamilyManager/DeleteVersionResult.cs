namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of deleting a non-active catalog version.
/// </summary>
/// <param name="Success">Whether the deletion succeeded at the database level.</param>
/// <param name="CatalogItemId">Catalog item whose version was deleted.</param>
/// <param name="VersionLabel">Version label that was deleted.</param>
/// <param name="VersionsDeleted">Number of catalog_versions rows deleted (1 per Revit variant of the label).</param>
/// <param name="AssetsDeleted">Number of family_assets rows explicitly deleted for this version label.</param>
/// <param name="FilesDeleted">Whether the physical files on disk were deleted (false if locked by Revit).</param>
/// <param name="PhysicalDirectoryPath">Absolute path of the deleted version directory (for diagnostics if file deletion failed).</param>
/// <param name="ErrorMessage">Error message if failed.</param>
public sealed record DeleteVersionResult(
    bool Success,
    string CatalogItemId,
    string VersionLabel,
    int VersionsDeleted,
    int AssetsDeleted,
    bool FilesDeleted,
    string? PhysicalDirectoryPath,
    string? ErrorMessage = null);
