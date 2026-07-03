namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of switching the active version of a catalog item.
/// </summary>
/// <param name="Success">Whether the switch succeeded.</param>
/// <param name="CatalogItemId">Catalog item whose active version was switched.</param>
/// <param name="VersionLabel">Version label that became active.</param>
/// <param name="PreviousVersionLabel">Version label that was active before the switch (for audit/undo).</param>
/// <param name="ActivatedAtUtc">UTC timestamp of the switch.</param>
/// <param name="ContentHashSynced">Whether <c>catalog_items.content_hash</c> was synchronized with the activated version's content_hash.</param>
/// <param name="ErrorMessage">Error message if failed.</param>
public sealed record SetActiveVersionResult(
    bool Success,
    string CatalogItemId,
    string VersionLabel,
    string? PreviousVersionLabel,
    DateTimeOffset ActivatedAtUtc,
    bool ContentHashSynced,
    string? ErrorMessage = null);
