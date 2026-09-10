namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One catalog version whose managed file was not found on disk during
/// the hash-recalculation migration (Issue #126). Listed on the summary
/// screen; the user decides whether to purge these catalog rows or keep
/// them (the file may live on a temporarily unavailable drive).
/// </summary>
/// <param name="CatalogItemId">Catalog item that owns the version.</param>
/// <param name="ItemName">Display name of the catalog item.</param>
/// <param name="VersionLabel">Version label whose file is missing.</param>
/// <param name="FileName">Expected file name (basename).</param>
public sealed record HashRecalculationMissingFile(
    string CatalogItemId,
    string ItemName,
    string VersionLabel,
    string FileName);
