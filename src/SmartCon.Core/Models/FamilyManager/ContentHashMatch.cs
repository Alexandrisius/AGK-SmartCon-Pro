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
public sealed record ContentHashMatch(
    string CatalogItemId,
    string MatchedVersionLabel,
    bool IsCurrentVersion);
