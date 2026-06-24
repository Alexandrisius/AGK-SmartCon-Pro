using SmartCon.Core.Services.FamilyManager;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Represents a single file row in the batch import dialog.
/// </summary>
public sealed record FamilyBatchImportItem(
    string FilePath,
    string FileName,
    int RevitMajorVersion,
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId = null,
    string? ExistingVersionLabel = null,
    string? TargetCategoryId = null,
    string? TargetCategoryName = null,
    string FamilySource = "loadable",
    int? TypeCount = null,
    string? RevitCategory = null,
    string? OriginalSourcePath = null,
    IReadOnlyList<SelectedSystemType>? SourceTypes = null)
{
    /// <summary>User-selected action for this file.</summary>
    public FamilyBatchImportAction Action { get; set; } =
        FamilyBatchImportAction.IncrementVersion;

    /// <summary>User-selected target category for this file (overrides dialog-level category).</summary>
    public string? TargetCategoryId { get; set; } = TargetCategoryId;

    /// <summary>Human-readable name of the target category.</summary>
    public string? TargetCategoryName { get; set; } = TargetCategoryName;
}
