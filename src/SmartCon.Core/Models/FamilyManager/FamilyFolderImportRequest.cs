namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Request to import all .rfa files from a folder into the published catalog.
/// </summary>
/// <param name="FolderPath">Absolute path to the folder.</param>
/// <param name="RevitMajorVersion">Target Revit major version for all files.</param>
/// <param name="Recursive">Whether to search subfolders.</param>
/// <param name="Category">User-assigned category for all files.</param>
/// <param name="Tags">User-assigned tags for all files.</param>
/// <param name="Description">User-assigned description prefix for all files.</param>
public sealed record FamilyFolderImportRequest(
    string FolderPath,
    int RevitMajorVersion,
    bool Recursive,
    string? Category,
    IReadOnlyList<string>? Tags,
    string? Description);
