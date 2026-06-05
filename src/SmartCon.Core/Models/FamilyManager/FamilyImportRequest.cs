namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Request to import a single family file into the published catalog.
/// The file is copied into managed storage at the database root.
/// </summary>
/// <param name="FilePath">Absolute path to the source file (.rfa or .rvt).</param>
/// <param name="RevitMajorVersion">Target Revit major version (e.g. 2025).</param>
/// <param name="Category">User-assigned category.</param>
/// <param name="Tags">User-assigned tags.</param>
/// <param name="Description">User-assigned description.</param>
/// <param name="CategoryId">User-assigned category identifier (from categories table).</param>
/// <param name="FileName">
/// User-edited display name (without extension) for the catalog item and
/// the destination file in managed storage. If null, the source file's name
/// is used (legacy behaviour).
/// </param>
/// <param name="OriginalSourcePath">
/// Absolute path to the original .rfa from which <paramref name="FilePath"/>
/// was derived (e.g. before it was copied to a temp staging folder).
/// Used to locate a Type Catalog (.txt) sidecar that lives next to the
/// original file but is not copied alongside the temp .rfa. Pass <c>null</c>
/// when the import target is the original file (no temp staging was used).
/// </param>
public sealed record FamilyImportRequest(
    string FilePath,
    int RevitMajorVersion,
    string? Category,
    IReadOnlyList<string>? Tags,
    string? Description,
    string? CategoryId = null,
    string FamilySource = "loadable",
    string? RevitCategory = null,
    string? FileName = null,
    string? OriginalSourcePath = null);
