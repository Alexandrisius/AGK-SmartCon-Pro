namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Version of a catalog item, linked to a specific file.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="CatalogItemId">Parent catalog item ID.</param>
/// <param name="FileId">Associated file record ID.</param>
/// <param name="VersionLabel">Display version label (e.g. "v1", "2024.1").</param>
/// <param name="Sha256">SHA-256 hash of the file content.</param>
/// <param name="RevitMajorVersion">Revit major version if detected.</param>
/// <param name="TypesCount">Number of family types if extracted.</param>
/// <param name="ParametersCount">Number of family parameters if extracted.</param>
/// <param name="ImportedAtUtc">Import timestamp.</param>
public sealed record FamilyCatalogVersion(
    string Id,
    string CatalogItemId,
    string FileId,
    string VersionLabel,
    string Sha256,
    int? RevitMajorVersion,
    int? TypesCount,
    int? ParametersCount,
    DateTimeOffset ImportedAtUtc);
