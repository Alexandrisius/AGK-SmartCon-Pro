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
/// <param name="MatchedVersionLabel">
/// v2.0.0: when <see cref="Status"/> is <see cref="FamilyBatchImportStatus.Duplicate"/>,
/// the version label whose stored hash matched this row's hash (e.g. "v2").
/// Displayed in the dialog as "Duplicate (v2)". <c>null</c> otherwise.
/// </param>
/// <param name="IsCrossNameDuplicate">
/// Issue #126: <c>true</c> when the content hash matched an item whose
/// name differs from this row's file name (the file was renamed). The
/// batch dialog renders a warning icon with a tooltip for such rows.
/// </param>
/// <param name="MatchedItemName">
/// Issue #126: display name of the catalog item whose version matched
/// the content hash. Used by the cross-name duplicate tooltip.
/// <c>null</c> unless <see cref="Status"/> is Duplicate.
/// </param>
/// <param name="ExistingCategoryId">
/// Issue #135: real category of the existing catalog item this row
/// resolves to (<see cref="ExistingCatalogItemId"/>), independent of
/// <see cref="TargetCategoryId"/> — the target may be overridden by the
/// «Импорт в категорию» command or the picker. Used by the batch dialog
/// to warn that a locked target category will MOVE the existing family
/// between categories on import.
/// </param>
/// <param name="ExistingCategoryPath">
/// Issue #135: human-readable path of <see cref="ExistingCategoryId"/>
/// for the move-warning tooltip.
/// </param>
/// <param name="LoadableSnapshot">
/// Phase 27: in-memory snapshot of the loadable family extracted during
/// Phase 1 Prepare. Survives the dialog round-trip so Phase 3 Commit can
/// write types + parameter values to the catalog WITHOUT re-opening the
/// managed .rfa. <c>null</c> for system families or when Prepare failed.
/// </param>
/// <param name="SystemSnapshot">
/// Phase 27: in-memory snapshot of the system family extracted during
/// Phase 1 Prepare. Survives the dialog round-trip so Phase 3 Commit can
/// write types + parameter values WITHOUT re-opening the staged .rvt.
/// <c>null</c> for loadable families or when Prepare failed.
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
    string? MatchedVersionLabel = null,
    FamilySnapshot? LoadableSnapshot = null,
    SystemFamilySnapshot? SystemSnapshot = null,
    string? PublishedBy = null,
    IReadOnlyList<FamilyGeometryPerType>? GeometryPerType = null,
    bool IsCrossNameDuplicate = false,
    string? MatchedItemName = null,
    string? ExistingCategoryId = null,
    string? ExistingCategoryPath = null,
    FamilyHealthReport? HealthReport = null)
{
    /// <summary>User-selected action for this file.</summary>
    public FamilyBatchImportAction Action { get; set; } =
        FamilyBatchImportAction.IncrementVersion;

    /// <summary>User-selected target category for this file (overrides dialog-level category).</summary>
    public string? TargetCategoryId { get; set; } = TargetCategoryId;

    /// <summary>Human-readable name of the target category.</summary>
    public string? TargetCategoryName { get; set; } = TargetCategoryName;

    /// <summary>Username of the Revit user who publishes this version (ADR-041 rev #5).</summary>
    public string? PublishedByUser { get; set; } = PublishedBy;
}
