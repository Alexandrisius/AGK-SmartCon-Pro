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
/// <param name="PrecomputedCatalogItemId">
/// v2.0.0: catalog item id that the VM computes up front (before the
/// batch dialog). For re-imports of an existing family, this is the
/// existing item's id; for new families, a freshly generated GUID. The
/// staging helper writes the staged <c>.rvt</c>/<c>.rfa</c> to the
/// canonical managed path derived from this id
/// (<c>{dbRoot}/files/&lt;PrecomputedCatalogItemId&gt;/&lt;PrecomputedVersionLabel&gt;/&lt;name&gt;.{ext}</c>),
/// and <see cref="IFamilyImportService.ImportFileAsync"/> uses it
/// directly when registering the catalog row. This is the only way to
/// keep the invariant
/// <c>family_files.relative_path = "{dbRoot}/files/&lt;catalogItemId&gt;/&lt;versionLabel&gt;/&lt;name&gt;"</c>
/// consistent for staged UC-2/UC-3/UC-4 files.
/// </param>
/// <param name="PrecomputedVersionLabel">
/// v2.0.0: version label the VM computes up front. <c>"v1"</c> for a new
/// item, <c>GetNextVersionLabelAsync(existingItem.Id)</c> for a
/// re-import (v2, v3, ...). See <see cref="PrecomputedCatalogItemId"/>
/// for the rationale.
/// </param>
/// <param name="PrecomputedManagedPath">
/// v2.0.0: absolute path to the canonical managed file. The staging
/// helper writes here, and <see cref="IFamilyImportService.ImportFileAsync"/>
/// uses this as <c>managedRfaPath</c>. Computed by the VM via
/// <see cref="SmartCon.FamilyManager.Services.LocalCatalog.StoragePathResolver"/>
/// using <see cref="PrecomputedCatalogItemId"/> +
/// <see cref="PrecomputedVersionLabel"/> + file name.
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
    FamilyImportSource? Source = null,
    string? PrecomputedCatalogItemId = null,
    string? PrecomputedVersionLabel = null,
    string? PrecomputedManagedPath = null,
    string? ContentHash = null,
    int? HashFormatVersion = null,
    string? MatchedVersionLabel = null)
{
    /// <summary>User-selected action for this file.</summary>
    public FamilyBatchImportAction Action { get; set; } =
        FamilyBatchImportAction.IncrementVersion;

    /// <summary>User-selected target category for this file (overrides dialog-level category).</summary>
    public string? TargetCategoryId { get; set; } = TargetCategoryId;

    /// <summary>Human-readable name of the target category.</summary>
    public string? TargetCategoryName { get; set; } = TargetCategoryName;
}
