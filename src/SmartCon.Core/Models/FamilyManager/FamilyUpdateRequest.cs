namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// v2.0.0: adds <see cref="PrecomputedVersionLabel"/> and
/// <see cref="PrecomputedManagedPath"/> to the legacy update request.
/// The VM populates them from the same lookup that
/// <see cref="FamilyBatchImportItem.PrecomputedCatalogItemId"/> uses, so
/// the import service can keep the managed-path invariant consistent for
/// re-imports of existing items.
/// </summary>
public sealed record FamilyUpdateRequest(
    string CatalogItemId,
    string FilePath,
    int RevitMajorVersion,
    string? CategoryId = null,
    string? CategoryName = null,
    string? FileName = null,
    string? OriginalSourcePath = null,
    string? PrecomputedVersionLabel = null,
    string? PrecomputedManagedPath = null,
    string? ContentHash = null,
    int? HashFormatVersion = null,
    string? PublishedBy = null,
    IReadOnlyList<FamilyGeometryPerType>? PreextractedGeometry = null,
    string? RevitCategory = null,
    int? RevitCategoryId = null,
    IReadOnlyList<FamilyFact>? Facts = null);
