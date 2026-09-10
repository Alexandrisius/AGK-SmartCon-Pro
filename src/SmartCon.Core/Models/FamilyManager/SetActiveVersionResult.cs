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
/// <param name="NameChanged">Issue #126: <c>true</c> when the catalog item's
/// name was updated to match the activated version's file name (the item
/// name always follows the ACTIVE version's file name).</param>
/// <param name="PreviousName">Item name before the switch, or <c>null</c>
/// when the item row was not found.</param>
/// <param name="NewName">Item name after the switch (equals the activated
/// version's file name without extension), or <c>null</c> when the name
/// could not be resolved (missing file record).</param>
public sealed record SetActiveVersionResult(
    bool Success,
    string CatalogItemId,
    string VersionLabel,
    string? PreviousVersionLabel,
    DateTimeOffset ActivatedAtUtc,
    bool ContentHashSynced,
    string? ErrorMessage = null,
    bool NameChanged = false,
    string? PreviousName = null,
    string? NewName = null);
