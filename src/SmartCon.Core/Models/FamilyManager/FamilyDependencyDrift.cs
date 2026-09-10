namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One drifted dependency link (E2, #209, schema V30): the parent's current
/// version embeds child version <see cref="EmbeddedVersionLabel"/>, but the
/// child's current active version is <see cref="CurrentVersionLabel"/> — a
/// newer child version was activated after the parent was imported. The
/// parent shows the "требует переимпорта" badge and its load is blocked
/// until it is re-imported with the up-to-date nested content.
/// </summary>
/// <param name="ParentCatalogItemId">Parent catalog item id.</param>
/// <param name="ChildCatalogItemId">Child catalog item id.</param>
/// <param name="ChildName">Child item display name (for messages/tooltips).</param>
/// <param name="EmbeddedVersionLabel">Child version embedded in the parent's current version.</param>
/// <param name="CurrentVersionLabel">Child's current active version label.</param>
public sealed record FamilyDependencyDrift(
    string ParentCatalogItemId,
    string ChildCatalogItemId,
    string ChildName,
    string EmbeddedVersionLabel,
    string CurrentVersionLabel);
