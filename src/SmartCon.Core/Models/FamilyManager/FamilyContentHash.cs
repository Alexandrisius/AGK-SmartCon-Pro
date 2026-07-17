namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Semantic content fingerprint of a family. Stable across SaveAs,
/// rename, Revit upgrade. Changes when any parameter, type, value,
/// geometry or formula changes.
/// </summary>
/// <param name="HexString">SHA-256 hex string (uppercase, no dashes).</param>
/// <param name="FormatVersion">Algorithm version. Bumped when the
/// canonical-string format changes so old hashes do not produce false
/// duplicate matches against new ones.</param>
/// <param name="SourceKind"><c>"loadable"</c> or <c>"system"</c>. Used
/// to enforce cross-source separation (system hashes never match
/// loadable hashes and vice versa).</param>
public sealed record FamilyContentHash(
    string HexString,
    int FormatVersion,
    string SourceKind);

/// <summary>
/// Current content-hash format version. Bump when the canonical string
/// layout changes (new fields, new ordering, new normalization). Old
/// rows with a lower <see cref="FamilyContentHash.FormatVersion"/> will
/// not produce false duplicate matches against newly computed hashes.
/// </summary>
/// <remarks>
/// Version history:
/// 1 — initial format. Loadable canonical string included the family
///     name (<c>FHV1|LOADABLE|{name}|{cat}|...</c>), so renamed files
///     produced different hashes and cross-name duplicates were
///     invisible to dedup.
/// 2 — rename-invariant (Issue #126). Loadable canonical string drops
///     the family name (<c>FHV2|LOADABLE|{cat}|...</c>). System
///     canonical string is unchanged (<c>FHV1|SYSTEM|...</c>) because
///     it never contained a name — system rows are migrated by a cheap
///     flag update without recomputation.
/// -1 (<see cref="RecalculationSkipped"/>) — sentinel written by the
///     hash-recalculation migration for versions whose file is
///     permanently unreadable (corrupt, Revit API failure). Skipped
///     rows are excluded from the pending-migration count so the
///     migration dialog does not reappear forever.
/// </remarks>
public static class FamilyContentHashFormat
{
    public const int CurrentVersion = 2;

    /// <summary>
    /// Sentinel <c>hash_format_version</c> for versions the migration
    /// could not recalculate and will never retry (unreadable file).
    /// Rows with this value never match dedup queries (same effect as
    /// a stale format) and are excluded from the pending count.
    /// </summary>
    public const int RecalculationSkipped = -1;
}
