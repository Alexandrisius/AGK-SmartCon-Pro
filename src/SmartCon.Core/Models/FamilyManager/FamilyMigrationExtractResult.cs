namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of a single-file snapshot extraction for the hash-recalculation
/// migration (Issue #126). Never throws across the boundary — a failure
/// is reported via <see cref="ErrorMessage"/> so the batch continues.
/// </summary>
/// <param name="Success">Whether the snapshot was extracted.</param>
/// <param name="LoadableSnapshot">Extracted loadable snapshot on success;
/// otherwise <c>null</c>.</param>
/// <param name="ErrorMessage">Short failure description on failure;
/// otherwise <c>null</c>.</param>
public sealed record FamilyMigrationExtractResult(
    bool Success,
    FamilySnapshot? LoadableSnapshot,
    string? ErrorMessage)
{
    public static FamilyMigrationExtractResult Ok(FamilySnapshot snapshot)
        => new(true, snapshot, null);

    public static FamilyMigrationExtractResult Fail(string errorMessage)
        => new(false, null, errorMessage);
}
