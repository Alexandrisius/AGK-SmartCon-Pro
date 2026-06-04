namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Represents a single file row in the batch import dialog.
/// </summary>
public sealed record FamilyBatchImportItem(
    string FilePath,
    string FileName,
    string Sha256,
    int RevitMajorVersion,
    long FileSizeBytes,
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId = null,
    string? ExistingVersionLabel = null,
    string? TargetCategoryId = null,
    string? TargetCategoryName = null,
    string FamilySource = "loadable",
    int TypeCount = 0,
    string? RevitCategory = null)
{
    /// <summary>User-selected action for this file.</summary>
    public FamilyBatchImportAction Action { get; set; } =
        Status == FamilyBatchImportStatus.Duplicate
            ? FamilyBatchImportAction.Skip
            : FamilyBatchImportAction.IncrementVersion;

    /// <summary>User-selected target category for this file (overrides dialog-level category).</summary>
    public string? TargetCategoryId { get; set; } = TargetCategoryId;

    /// <summary>Human-readable name of the target category.</summary>
    public string? TargetCategoryName { get; set; } = TargetCategoryName;
}
