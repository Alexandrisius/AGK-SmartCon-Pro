namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Version of a catalog item for a specific Revit major version.
/// Multiple records with the same VersionLabel but different RevitMajorVersion
/// represent the same logical version compiled for different Revit releases.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="CatalogItemId">Parent catalog item ID.</param>
/// <param name="FileId">Associated file record ID.</param>
/// <param name="VersionLabel">Display version label (e.g. "v1", "v2").</param>
/// <param name="Sha256">SHA-256 hash of the file content.</param>
/// <param name="RevitMajorVersion">Target Revit major version.</param>
/// <param name="TypesCount">Number of family types if extracted.</param>
/// <param name="ParametersCount">Number of family parameters if extracted.</param>
/// <param name="PublishedAtUtc">When this version was published.</param>
public sealed record FamilyCatalogVersion(
    string Id,
    string CatalogItemId,
    string FileId,
    string VersionLabel,
    string Sha256,
    int RevitMajorVersion,
    int? TypesCount,
    int? ParametersCount,
    DateTimeOffset PublishedAtUtc);
