namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Normalized outcome of a single database migration run (ADR-054). Every
/// migration maps its internal result to this shape so the unified update
/// dialog can render a combined summary.
/// </summary>
/// <param name="UpdatedCount">Records successfully processed.</param>
/// <param name="NewerRevitCount">Records left pending because their file
/// variants require a newer Revit.</param>
/// <param name="MissingFiles">Managed files not found on disk.</param>
/// <param name="FailedFiles">Files that opened but failed extraction.</param>
/// <param name="WasCancelled">True when the user cancelled mid-run —
/// committed batches stay, the rest resumes on the next run.</param>
public sealed record DatabaseMigrationResult(
    int UpdatedCount,
    int NewerRevitCount,
    IReadOnlyList<HashRecalculationMissingFile> MissingFiles,
    IReadOnlyList<HashRecalculationFailedFile> FailedFiles,
    bool WasCancelled)
{
    /// <summary>Empty result used when a run is cancelled before start or fails wholesale.</summary>
    public static DatabaseMigrationResult Cancelled { get; } = new(
        0, 0,
        Array.Empty<HashRecalculationMissingFile>(),
        Array.Empty<HashRecalculationFailedFile>(),
        WasCancelled: true);
}
