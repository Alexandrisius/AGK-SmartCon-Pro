namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of importing a single family file.
/// </summary>
/// <param name="Success">Whether the import succeeded.</param>
/// <param name="CatalogItemId">ID of the created or updated catalog item.</param>
/// <param name="VersionId">ID of the created version.</param>
/// <param name="FileId">ID of the created file record.</param>
/// <param name="FileName">Imported file name.</param>
/// <param name="VersionLabel">Assigned version label (e.g. "v1", "v2").</param>
/// <param name="ErrorMessage">Error message if failed.</param>
/// <param name="WasSkippedAsDuplicate">True if skipped because same SHA256 already exists for this Revit version.</param>
/// <param name="WasNewVersion">True if a new version was created for an existing family.</param>
public sealed record FamilyImportResult(
    bool Success,
    string? CatalogItemId,
    string? VersionId,
    string? FileId,
    string? FileName,
    string? VersionLabel,
    string? ErrorMessage,
    bool WasSkippedAsDuplicate = false,
    bool WasNewVersion = false);
