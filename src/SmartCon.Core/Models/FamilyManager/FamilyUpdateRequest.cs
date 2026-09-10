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
    IReadOnlyList<FamilyFact>? Facts = null,
    /// <summary>
    /// Issue #249 (Phase 2): per-type content hashes from Prepare,
    /// written to <c>family_type_hashes</c> in the update transaction.
    /// <c>null</c> for legacy paths — backfilled by the optional
    /// <c>type-hashes-v1</c> actualization task.
    /// </summary>
    IReadOnlyList<FamilyTypeHashEntry>? PerTypeHashes = null,
    /// <summary>
    /// Issue #249 (Phase 4): canonical content sections from Prepare —
    /// see <c>FamilyImportRequest.Sections</c>.
    /// </summary>
    IReadOnlyList<ContentSectionHash>? Sections = null,
    /// <summary>
    /// Issue #261: the user explicitly picked «Без категории» in the batch
    /// dialog — the update must WRITE a NULL category (move the item out of
    /// its current category), as opposed to a null <see cref="CategoryId"/>
    /// without the flag, which means "no explicit choice — don't touch".
    /// </summary>
    bool ClearCategory = false);
