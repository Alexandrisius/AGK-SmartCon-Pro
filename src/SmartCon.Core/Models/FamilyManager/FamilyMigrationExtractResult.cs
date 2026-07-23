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
/// <param name="Geometry">Per-type geometry extracted in the same open
/// session (database actualization, ADR-054); <c>null</c> when geometry
/// was not requested or failed (the caller falls back to a dedicated
/// geometry extraction pass).</param>
/// <param name="SystemSnapshot">System-family snapshot extracted from a
/// staged mini-project (.rvt) on the system path (ADR-056); <c>null</c>
/// on the loadable (.rfa) path.</param>
public sealed record FamilyMigrationExtractResult(
    bool Success,
    FamilySnapshot? LoadableSnapshot,
    string? ErrorMessage,
    IReadOnlyList<FamilyGeometryPerType>? Geometry = null,
    SystemFamilySnapshot? SystemSnapshot = null)
{
    public static FamilyMigrationExtractResult Ok(FamilySnapshot snapshot)
        => new(true, snapshot, null);

    public static FamilyMigrationExtractResult Ok(
        FamilySnapshot snapshot, IReadOnlyList<FamilyGeometryPerType>? geometry)
        => new(true, snapshot, null, geometry);

    public static FamilyMigrationExtractResult OkSystem(
        FamilySnapshot loadableStub, SystemFamilySnapshot systemSnapshot)
        => new(true, loadableStub, null, null, systemSnapshot);

    public static FamilyMigrationExtractResult Fail(string errorMessage)
        => new(false, null, errorMessage);
}
