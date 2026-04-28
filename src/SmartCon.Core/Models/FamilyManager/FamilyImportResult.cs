namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of importing a single family file.
/// </summary>
/// <param name="Success">Whether the import succeeded.</param>
/// <param name="CatalogItemId">ID of the created catalog item.</param>
/// <param name="VersionId">ID of the created version.</param>
/// <param name="FileId">ID of the created file record.</param>
/// <param name="FileName">Imported file name.</param>
/// <param name="ErrorMessage">Error message if failed.</param>
/// <param name="WasSkippedAsDuplicate">True if skipped because hash matched existing.</param>
/// <param name="DuplicateCatalogItemId">ID of the existing item if duplicate.</param>
public sealed record FamilyImportResult(
    bool Success,
    string? CatalogItemId,
    string? VersionId,
    string? FileId,
    string? FileName,
    string? ErrorMessage,
    bool WasSkippedAsDuplicate = false,
    string? DuplicateCatalogItemId = null);
