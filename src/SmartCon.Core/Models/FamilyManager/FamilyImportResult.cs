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
/// <param name="ManagedFilePath">Absolute path to the file as registered in managed storage.</param>
/// <param name="WasSkippedAsDuplicate">True if the user selected Skip in the batch dialog (kept for backward-compat; no longer implies SHA256 dedup since v2.0.0).</param>
/// <param name="WasNewVersion">True if a new version was created for an existing family.</param>
public sealed record FamilyImportResult(
    bool Success,
    string? CatalogItemId,
    string? VersionId,
    string? FileId,
    string? FileName,
    string? VersionLabel,
    string? ErrorMessage,
    string? ManagedFilePath = null,
    bool WasSkippedAsDuplicate = false,
    bool WasNewVersion = false);
