namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of the catalog hash-recalculation migration (Issue #126).
/// </summary>
/// <param name="UpdatedCount">Versions whose hash was recalculated to
/// format v2 (all Revit variants of a processed label included).</param>
/// <param name="SystemRelabeledCount">System-family versions migrated by
/// a cheap flag update (their v1 hashes were already rename-invariant).</param>
/// <param name="NewerRevitCount">Versions left pending because they only
/// have file variants saved in a Revit version NEWER than the running
/// one. They will be offered again when the catalog is opened in a
/// newer Revit.</param>
/// <param name="MissingFiles">Versions whose managed file was not found.
/// NOT modified — the user decides (purge or keep).</param>
/// <param name="FailedFiles">Versions that could not be read; marked
/// <c>hash_format_version = -1</c> (never retried).</param>
/// <param name="WasCancelled"><c>true</c> when the user interrupted the
/// migration. Already-committed batches stay committed; the remaining
/// versions are offered again on the next prompt.</param>
public sealed record CatalogHashRecalculationResult(
    int UpdatedCount,
    int SystemRelabeledCount,
    int NewerRevitCount,
    IReadOnlyList<HashRecalculationMissingFile> MissingFiles,
    IReadOnlyList<HashRecalculationFailedFile> FailedFiles,
    bool WasCancelled);
