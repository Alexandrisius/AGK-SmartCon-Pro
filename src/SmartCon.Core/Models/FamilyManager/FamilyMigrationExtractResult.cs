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
/// <param name="SharedNestedSnapshots">FHV8 (#209): own snapshots of every
/// shared-nested family in the closure (each opened via EditFamily from
/// its parent document in the SAME open session), or <c>null</c> when the
/// family has no shared nested children. Used by the composite-hash
/// composition pass (<c>CompositeFamilyHashComposer</c>).</param>
/// <param name="SharedNestedSubtrees">FHV8 (#209): flat shared-nested
/// subtree scans — one entry per scanned document (the root family and
/// every opened nested), or <c>null</c> when there is nothing to scan.
/// The composer derives direct edges from these by subtraction.</param>
public sealed record FamilyMigrationExtractResult(
    bool Success,
    FamilySnapshot? LoadableSnapshot,
    string? ErrorMessage,
    IReadOnlyList<FamilyGeometryPerType>? Geometry = null,
    SystemFamilySnapshot? SystemSnapshot = null,
    IReadOnlyList<FamilySnapshot>? SharedNestedSnapshots = null,
    IReadOnlyList<SharedNestedSubtree>? SharedNestedSubtrees = null)
{
    public static FamilyMigrationExtractResult Ok(FamilySnapshot snapshot)
        => new(true, snapshot, null);

    public static FamilyMigrationExtractResult Ok(
        FamilySnapshot snapshot, IReadOnlyList<FamilyGeometryPerType>? geometry)
        => new(true, snapshot, null, geometry);

    public static FamilyMigrationExtractResult Ok(
        FamilySnapshot snapshot,
        IReadOnlyList<FamilyGeometryPerType>? geometry,
        IReadOnlyList<FamilySnapshot>? sharedNestedSnapshots,
        IReadOnlyList<SharedNestedSubtree>? sharedNestedSubtrees)
        => new(true, snapshot, null, geometry, null, sharedNestedSnapshots, sharedNestedSubtrees);

    public static FamilyMigrationExtractResult OkSystem(
        FamilySnapshot loadableStub, SystemFamilySnapshot systemSnapshot)
        => new(true, loadableStub, null, null, systemSnapshot);

    public static FamilyMigrationExtractResult Fail(string errorMessage)
        => new(false, null, errorMessage);
}

/// <summary>
/// FHV8 (#209): the FLAT shared-nested subtree scanned in one family
/// document (probe P1: every nesting level is visible flat). The root
/// family and every opened nested family each contribute one entry; the
/// composite-hash composer derives direct parent→child edges by
/// subtraction over these sets.
/// </summary>
/// <param name="OwnerFamilyName">Normalized name of the family whose
/// document was scanned.</param>
/// <param name="NestedFamilyNames">Normalized names of ALL shared nested
/// families visible in that document (flat, every level).</param>
public sealed record SharedNestedSubtree(
    string OwnerFamilyName,
    IReadOnlyList<string> NestedFamilyNames);
