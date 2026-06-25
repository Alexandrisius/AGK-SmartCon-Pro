namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Physical file record for a family (.rfa) in managed storage.
/// Files are stored at {db-root}/files/{family-id}/{version}/{fileName}.rfa.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="RelativePath">Relative path from database root to the file.</param>
/// <param name="FileName">File name with extension.</param>
/// <param name="RevitMajorVersion">Target Revit major version.</param>
/// <param name="ImportedAtUtc">When the file was imported into managed storage.</param>
public sealed record FamilyFileRecord(
    string Id,
    string RelativePath,
    string FileName,
    int RevitMajorVersion,
    DateTimeOffset ImportedAtUtc);
