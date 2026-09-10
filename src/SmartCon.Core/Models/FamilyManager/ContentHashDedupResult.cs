namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of the content-hash dedup check for a single batch-import row.
/// Combines the name-based lookup with the cross-version hash search to
/// produce the final <see cref="FamilyBatchImportStatus"/>.
/// </summary>
/// <param name="Status">Final status: <see cref="FamilyBatchImportStatus.New"/>,
/// <see cref="FamilyBatchImportStatus.Existing"/>,
/// <see cref="FamilyBatchImportStatus.Duplicate"/>, or
/// <see cref="FamilyBatchImportStatus.Error"/>.</param>
/// <param name="ExistingCatalogItemId">ID of the catalog item this row
/// resolves to. Issue #126: for <see cref="FamilyBatchImportStatus.Duplicate"/>
/// this is the item matched BY CONTENT HASH (which may have a different
/// name); for <see cref="FamilyBatchImportStatus.Existing"/> it is the
/// item matched by normalized name; <c>null</c> for New/Error.</param>
/// <param name="ExistingVersionLabel">Current version label of the
/// resolved item, or <c>null</c>.</param>
/// <param name="HashMatch">Cross-version hash match details if the
/// status is <see cref="FamilyBatchImportStatus.Duplicate"/>; otherwise
/// <c>null</c>.</param>
/// <param name="IsCrossNameDuplicate">Issue #126: <c>true</c> when the
/// content hash matched an item whose normalized name differs from this
/// row's normalized name (the file was renamed). The batch dialog shows
/// a warning icon with an explanatory tooltip for such rows.</param>
public sealed record ContentHashDedupResult(
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId,
    string? ExistingVersionLabel,
    ContentHashMatch? HashMatch,
    bool IsCrossNameDuplicate = false);
