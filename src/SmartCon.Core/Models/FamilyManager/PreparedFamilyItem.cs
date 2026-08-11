namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of Phase 1 (Prepare) of the unified import flow. Contains
/// everything the batch dialog needs to display a row and everything
/// Phase 3 (Commit) needs to write to the catalog — extracted in a
/// single pass from one opened document, without re-opening.
/// </summary>
/// <param name="SourcePath">Source file path for UC-1, or virtual
/// placeholder for UC-3/UC-4 (<c>"system://..."</c>,
/// <c>"loadable://..."</c>). For UC-2 (active .rfa) this is the
/// document's path.</param>
/// <param name="DisplayName">Display name for the batch dialog row
/// (file name without extension, or category name for system
/// families).</param>
/// <param name="RevitMajorVersion">Revit major version of the source
/// file.</param>
/// <param name="ContentHash">Computed content hash, or <c>null</c> if
/// extraction failed (see <see cref="ErrorMessage"/>).</param>
/// <param name="LoadableSnapshot">Snapshot for loadable families, or
/// <c>null</c> for system families.</param>
/// <param name="SystemSnapshot">Snapshot for system families, or
/// <c>null</c> for loadable families.</param>
/// <param name="ErrorMessage">Error message if hash/snapshot extraction
/// failed; <c>null</c> on success.</param>
/// <param name="Source">v2.0.0 source payload for UC-3/UC-4 post-dialog
/// staging; <c>null</c> for UC-1/UC-2.</param>
/// <param name="SourceTypes">Source type info for system families
/// (parallel to <see cref="FamilyBatchImportItem.SourceTypes"/>);
/// <c>null</c> for loadable families.</param>
/// <param name="FamilySource"><c>"loadable"</c> or <c>"system"</c>.</param>
/// <param name="RoutingDependencies">
/// ADR-066 (E1): dependency descriptors discovered from this item's routing
/// rules — set on SYSTEM items only, transient (Phase-1 → dependency
/// preparation inside the same call), never carried into the dialog.
/// </param>
/// <param name="DependencyLinks">
/// ADR-066: set on CHILD items (dependencies of another row) — the parent
/// rows this item must be linked to in <c>family_dependencies</c> after
/// import. <c>null</c> for top-level rows.
/// </param>
/// <param name="SharedNestedDependencies">
/// ADR-066 (E2, #209): shared-nested descriptors discovered in this item's
/// family document — set on LOADABLE items only, transient (Phase-1 →
/// nested preparation inside the same call), never carried into the dialog.
/// <see cref="FamilyDependencyDescriptor.FamilyUniqueId"/> values are valid
/// in THIS item's held-open family document only.
/// </param>
public sealed record PreparedFamilyItem(
    string SourcePath,
    string DisplayName,
    int RevitMajorVersion,
    FamilyContentHash? ContentHash,
    FamilySnapshot? LoadableSnapshot,
    SystemFamilySnapshot? SystemSnapshot,
    string? ErrorMessage,
    FamilyImportSource? Source,
    IReadOnlyList<FamilySourceTypeInfo>? SourceTypes,
    string FamilySource,
    FamilyBatchImportStatus Status = FamilyBatchImportStatus.New,
    string? ExistingCatalogItemId = null,
    string? ExistingVersionLabel = null,
    string? MatchedVersionLabel = null,
    IReadOnlyList<FamilyGeometryPerType>? GeometryPerType = null,
    bool IsCrossNameDuplicate = false,
    string? MatchedItemName = null,
    FamilyHealthReport? HealthReport = null,
    IReadOnlyList<FamilyDependencyDescriptor>? RoutingDependencies = null,
    IReadOnlyList<FamilyDependencyLink>? DependencyLinks = null,
    IReadOnlyList<FamilyDependencyDescriptor>? SharedNestedDependencies = null);
