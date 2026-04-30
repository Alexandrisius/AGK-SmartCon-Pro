namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Request to import a single family file into the published catalog.
/// The file is copied into managed storage at the database root.
/// </summary>
/// <param name="FilePath">Absolute path to the .rfa file.</param>
/// <param name="RevitMajorVersion">Target Revit major version (e.g. 2025).</param>
/// <param name="Category">User-assigned category.</param>
/// <param name="Tags">User-assigned tags.</param>
/// <param name="Description">User-assigned description.</param>
public sealed record FamilyImportRequest(
    string FilePath,
    int RevitMajorVersion,
    string? Category,
    IReadOnlyList<string>? Tags,
    string? Description);
