namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of a cross-version content-hash search. Returned when a
/// content hash matches a version (current or archived) of a catalog
/// item. Used to display "Дубликат (vN)" in the batch dialog.
/// </summary>
/// <param name="CatalogItemId">ID of the catalog item whose version
/// matched the hash.</param>
/// <param name="MatchedVersionLabel">Label of the matching version
/// (e.g. "v2").</param>
/// <param name="IsCurrentVersion"><c>true</c> if the matched version is
/// the current version of the catalog item; <c>false</c> if it is an
/// archived version (e.g. after rollback).</param>
/// <param name="CurrentVersionLabel">Current active version label of the
/// matched catalog item, or <c>null</c> if the item has none. Issue
/// #126: with hash-first dedup the matched item is the canonical
/// "existing" item for downstream actions, so its active label must
/// travel with the match.</param>
/// <param name="MatchedItemName">Display name of the matched catalog
/// item. Differs from the imported file name for cross-name duplicates
/// (rename-invariant hash, Issue #126).</param>
/// <param name="MatchedItemNormalizedName">Normalized name of the
/// matched catalog item. Compared against the imported row's normalized
/// name to detect a cross-name duplicate.</param>
public sealed record ContentHashMatch(
    string CatalogItemId,
    string MatchedVersionLabel,
    bool IsCurrentVersion,
    string? CurrentVersionLabel,
    string MatchedItemName,
    string MatchedItemNormalizedName);
