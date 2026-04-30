namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Record of a family being loaded into a Revit project.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="CatalogItemId">Catalog item that was loaded.</param>
/// <param name="VersionId">Version that was loaded.</param>
/// <param name="ProjectName">Revit project file name.</param>
/// <param name="ProjectPath">Revit project file path.</param>
/// <param name="RevitMajorVersion">Revit version used for loading.</param>
/// <param name="Action">Action performed (e.g. "Load", "LoadAndPlace").</param>
/// <param name="CreatedAtUtc">When the action occurred.</param>
public sealed record ProjectFamilyUsage(
    string Id,
    string CatalogItemId,
    string? VersionId,
    string? ProjectName,
    string? ProjectPath,
    int? RevitMajorVersion,
    string Action,
    DateTimeOffset CreatedAtUtc);
