namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Resolved file path ready for loading into Revit.
/// </summary>
/// <param name="AbsolutePath">Absolute path to the .rfa file.</param>
/// <param name="CatalogItemId">Associated catalog item ID.</param>
/// <param name="VersionId">Associated version ID.</param>
public sealed record FamilyResolvedFile(
    string AbsolutePath,
    string? CatalogItemId,
    string? VersionId);
