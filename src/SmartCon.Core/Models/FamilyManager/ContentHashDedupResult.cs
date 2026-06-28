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
/// <param name="ExistingCatalogItemId">ID of the existing catalog item
/// found by normalized name, or <c>null</c> if the name is not in the
/// catalog.</param>
/// <param name="ExistingVersionLabel">Current version label of the
/// existing item, or <c>null</c>.</param>
/// <param name="HashMatch">Cross-version hash match details if the
/// status is <see cref="FamilyBatchImportStatus.Duplicate"/>; otherwise
/// <c>null</c>.</param>
public sealed record ContentHashDedupResult(
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId,
    string? ExistingVersionLabel,
    ContentHashMatch? HashMatch);
