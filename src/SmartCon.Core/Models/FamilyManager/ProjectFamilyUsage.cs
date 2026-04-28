namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Record of a family being loaded into a Revit project.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="CatalogItemId">Catalog item that was loaded.</param>
/// <param name="VersionId">Version that was loaded.</param>
/// <param name="ProviderId">Provider that supplied the family.</param>
/// <param name="ProjectFingerprint">Project identifier (file path hash or similar).</param>
/// <param name="Action">Action performed (e.g. "Load", "Reload").</param>
/// <param name="CreatedAtUtc">When the action occurred.</param>
public sealed record ProjectFamilyUsage(
    string Id,
    string CatalogItemId,
    string VersionId,
    string ProviderId,
    string ProjectFingerprint,
    string Action,
    DateTimeOffset CreatedAtUtc);
