namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One catalog version whose file exists but could not be read during
/// the hash-recalculation migration (corrupt file, Revit API failure).
/// Such versions are permanently marked
/// <c>hash_format_version = -1</c> so the migration never retries them.
/// </summary>
/// <param name="ItemName">Display name of the catalog item.</param>
/// <param name="VersionLabel">Version label that failed.</param>
/// <param name="FileName">File name (basename).</param>
/// <param name="ErrorMessage">Short failure description.</param>
public sealed record HashRecalculationFailedFile(
    string ItemName,
    string VersionLabel,
    string FileName,
    string ErrorMessage);
