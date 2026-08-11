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
/// 3 — content coverage v3 (Issue #159, ADR-056). Loadable: category
///     ordinal replaces the locale-dependent display name; new sections
///     FACTS (Part Type), FLAGS (behavior), CONN (connectors), geometry
///     gains bounding box + surface area + curve lengths, non-shared
///     nested families join NESTED; values are escaped. System:
///     <c>FHV3|SYSTEM|{catId}|...</c> drops the locale-dependent
///     category name and gains per-type STRUCT (compound layers) and
///     ROUTING (routing preferences) sections. Both sources need a
///     full file-based recomputation (critical task <c>hash-v3</c>).
/// 4 — system identity coverage (Issues #184/#179/#190, ADR-065).
///     System only: <c>FHV4|SYSTEM|{catId}|...</c> adds per-type FAMKEY
///     (locale-invariant family key), STRUCT gains
///     StructuralMaterialIndex/EndCap/OpeningWrapping and per-layer
///     LayerCapFlag/ParticipatesInWrapping, new SEGMENTS (size tables),
///     SUBTYPES (stairs subtype references by name) and RAILING
///     (structure summary) sections. Loadable canonical string is
///     unchanged (FHV3 loadable rows are re-stamped to 4 by the same
///     critical task (superseded by <c>hash-v5</c> before any public release) — recompute is file-based for both
///     sources, so no cheap flag pass is needed).
    /// 5 — wire settings coverage (manual test 2026-08-04). System only:
    ///     <c>FHV5|SYSTEM|{catId}|...</c> adds the per-type WIRE section
    ///     (wire material / temperature rating / insulation / max size /
    ///     conduit / neutral scalars) — that data lives on WireType API
    ///     properties and never appears in Element.Parameters, so FHV4
    ///     could not detect a wire material change. Critical task
    ///     <c>hash-v5</c> recomputes every row that is not current.
    /// 6 — deterministic TYPES ordering (stress test 2026-08-05). System
    ///     only: <c>FHV6|SYSTEM|{catId}|...</c> — the type sort gains
    ///     (FamilyKey, FamilyName) tie-breaks: OrderBy is a stable sort,
    ///     so same-named types of different families (both conduit
    ///     families name their type «Короб») kept the extraction order,
    ///     which differs between the source project and the staged
    ///     mini-project — identical content produced different hashes and
    ///     a phantom "Существующая" instead of "Дубликат". Critical task
    ///     <c>hash-v6</c> recomputes every row that is not current.
    /// 7 — duct shape identity (#215, manual test 2023 2026-08-06). System
    ///     only: <c>FHV7|SYSTEM|{catId}|...</c> — DuctType leaves
    ///     SingleFamily and gains the Shape discriminator
    ///     (Duct.Round/Rectangular/Oval): the "Воздуховоды" category has
    ///     THREE system families, and the shared "Single" key let a
    ///     rectangular template prototype match a round reference — the
    ///     sync created a type of the wrong shape with UI-incompatible
    ///     fittings. Critical task <c>hash-v7</c> recomputes every row
    ///     that is not current AND heals family_types.family_key from the
    ///     staged snapshot (stored "Single" keys of ducts are rewritten to
    ///     the shape keys).
    /// 8 — composite nested content (#209, ADR-066, owner decision
    ///     2026-08-07). Loadable only: <c>FHV8|LOADABLE|{catOrdinal}|...</c>
    ///     gains the NESTEDHASH section — direct shared-nested children as
    ///     sorted (name, composite-hash) pairs. A content change inside a
    ///     shared nested family transitively shifts the hashes of every
    ///     ancestor (bolt → flange → valve), so a parent re-imported with
    ///     updated nested content yields a NEW VERSION instead of a false
    ///     Duplicate. Composition is bottom-up over direct edges derived
    ///     from the flat per-document subtree scans
    ///     (<c>CompositeFamilyHashComposer</c>); non-shared nested stay
    ///     name-only (#217 tracks promoting them to catalog components).
    ///     Same bump: the unnamed default TYPE is no longer extracted as
    ///     the synthetic <c>&lt;default&gt;</c> — Revit synthesizes it when
    ///     a typeless family is LOADED into a document (raw .rfa reports
    ///     Types.Size=0, an EditFamily copy Size=1), so extracting it made
    ///     the hash depend on the extraction context and broke
    ///     import↔migration and file↔nested dedup equality (caught by the
    ///     FHV8 integration probe). Critical task <c>hash-v8</c> recomputes
    ///     every row that is not current (system rows re-stamp, loadable
    ///     rows get the composite hash — nested closures are extracted from
    ///     the managed .rfa itself, no cross-group ordering needed).
/// -1 (<see cref="RecalculationSkipped"/>) — sentinel written by the
///     hash-recalculation migration for versions whose file is
///     permanently unreadable (corrupt, Revit API failure). Skipped
///     rows are excluded from the pending-migration count so the
///     migration dialog does not reappear forever.
/// -2 (<see cref="RecalculationMissing"/>) — sentinel written by the
///     migration for versions whose managed file is absent from disk.
///     The migration cannot process them by definition, so they are
///     excluded from the pending count (the update banner is about hash
///     freshness, not file hygiene) — the user decides: purge the
///     catalog rows from the summary screen, or restore the files and
///     re-import.
/// </remarks>
public static class FamilyContentHashFormat
{
    public const int CurrentVersion = 8;

    /// <summary>
    /// Sentinel <c>hash_format_version</c> for versions the migration
    /// could not recalculate and will never retry (unreadable file).
    /// Rows with this value never match dedup queries (same effect as
    /// a stale format) and are excluded from the pending count.
    /// </summary>
    public const int RecalculationSkipped = -1;

    /// <summary>
    /// Sentinel <c>hash_format_version</c> for versions whose managed
    /// file was not found on disk during the migration. Excluded from
    /// the pending count (the migration cannot recalculate what it
    /// cannot open) but kept in the catalog for the user's purge /
    /// restore decision.
    /// </summary>
    public const int RecalculationMissing = -2;
}
