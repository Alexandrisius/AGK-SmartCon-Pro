namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Represents a single file row in the batch import dialog.
/// </summary>
/// <param name="FilePath">
/// Path to the file. For UC-1/UC-2 it is a real file path. For UC-3/UC-4
/// (active project / selected elements) the row is built BEFORE staging
/// and the path is a virtual placeholder like
/// <c>"system://OST_Pipes"</c> or <c>"loadable://FamilyName"</c>. The
/// real managed path is allocated by the orchestrator AFTER the user
/// confirms the dialog.
/// </param>
/// <param name="Source">
/// v2.0.0: payload used by the post-dialog staging flow to create the
/// managed file. <c>null</c> for UC-1/UC-2 (the file already exists on
/// disk). Set to a <see cref="FamilyImportSource.SystemSource"/> or
/// <see cref="FamilyImportSource.LoadableSource"/> for UC-3/UC-4.
/// </param>
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
    IReadOnlyList<FamilySourceTypeInfo>? SourceTypes = null,
    FamilyImportSource? Source = null)
{
    /// <summary>User-selected action for this file.</summary>
    public FamilyBatchImportAction Action { get; set; } =
        FamilyBatchImportAction.IncrementVersion;

    /// <summary>User-selected target category for this file (overrides dialog-level category).</summary>
    public string? TargetCategoryId { get; set; } = TargetCategoryId;

    /// <summary>Human-readable name of the target category.</summary>
    public string? TargetCategoryName { get; set; } = TargetCategoryName;
}
