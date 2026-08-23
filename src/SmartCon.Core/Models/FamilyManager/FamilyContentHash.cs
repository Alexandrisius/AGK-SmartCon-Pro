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
    /// 9 — phantom value coverage (#209 stress test 2026-08-12). Loadable
    ///     only: <c>FHV9|LOADABLE|{catOrdinal}|...</c> gains the PHANTOM
    ///     section — parameter values of a TYPELESS family, read from the
    ///     unnamed current type or a synthesized Transaction+RollBack type
    ///     so both extraction contexts (raw open vs EditFamily copy) agree.
    ///     FHV8 skipped the phantom entirely, so an edit of any
    ///     non-geometric value on a typeless family (e.g. the built-in
    ///     «Модель») never shifted the hash and the import dialog reported
    ///     a false Duplicate. Critical task <c>hash-v9</c> recomputes
    ///     every row that is not current.
    /// 10 — parameter groups leave the hash (owner decision 2026-08-12):
    ///     <c>FHV10|LOADABLE|...</c> drops the ParameterGroup field from
    ///     PARAMS. Groups are the only content a reload merge physically
    ///     cannot transfer (probe-proven: UI/plain-merge 2026-08-11,
    ///     poke + doc-to-doc 2026-08-12) while the embedded EditFamily
    ///     document is otherwise byte-identical to the source file
    ///     (DrivenEmbeddedPollutionProbeTests — host associations live on
    ///     instances, not in the embedded definition). Keeping groups in
    ///     identity forked one identification into two divergent grades
    ///     (marker-vs-hash contradictions, «Duplicate (v2)» on v1-group
    ///     content). ONE hash now serves import dedup, versioning,
    ///     embedded verification and stale-check content proofs; a
    ///     group-only edit no longer version-bumps (accepted product
    ///     tradeoff: embedded groups cannot be updated anyway). Version
    ///     pairs that differed ONLY by groups collapse to one hash after
    ///     the recalculation — deterministic (dedup resolves to one of
    ///     them), documented in ADR-068 addendum. Critical task
    ///     <c>hash-v10</c> recomputes every row that is not current.
    /// 11 — lookup tables enter the hash (Issue #238, ADR-069): loadable
    ///     only: <c>FHV11|LOADABLE|...</c> gains the LOOKUP section — raw
    ///     CSV content (normalized) of the family's embedded lookup tables
    ///     (таблицы поиска, <c>FamilySizeTable</c>), tables sorted by name.
    ///     A values-only edit of a lookup table previously never shifted
    ///     the hash and the import dialog reported a false Duplicate.
    ///     Merge-safe by probe (2026-08-23): a reload merge transfers
    ///     lookup-table content into the embedded copy both in a project
    ///     and in a doc-to-doc load, so the single unified hash stays
    ///     consistent. The section is omitted for table-less families.
    ///     Critical task <c>hash-v11</c> recomputes every row that is not
    ///     current.
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
    public const int CurrentVersion = 11;

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
