using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Computes a stable <see cref="FamilyContentHash"/> from a snapshot.
/// Pure C# — no Revit API calls. The hash is a SHA-256 of a canonical
/// string built from the snapshot data. Stable across SaveAs, rename,
/// and Revit upgrade because it is based on in-memory content, not file
/// bytes.
/// </summary>
public interface IFamilyContentHasher
{
    /// <summary>
    /// Compute a content hash for a loadable family (.rfa) snapshot.
    /// Returns <c>null</c> if the snapshot is null or empty (no parameters
    /// and no types and no geometry).
    /// <para>
    /// FHV10 (owner decision 2026-08-12): this ONE hash serves every
    /// purpose — import dedup, versioning, embedded verification after a
    /// nested reload and stale-check content proofs. Parameter groups are
    /// the only excluded content field (no merge ever propagates them —
    /// probe-proven); the embedded EditFamily document is byte-identical
    /// to the source file in every other respect (probe-proven:
    /// associations live on instances in the host document), so a single
    /// canonical string is fair in all extraction contexts.
    /// </para>
    /// </summary>
    FamilyContentHash? ComputeForLoadable(FamilySnapshot snapshot);

    /// <summary>
    /// Compute a content hash for a system family snapshot. Returns
    /// <c>null</c> if the snapshot is null or has no types.
    /// </summary>
    FamilyContentHash? ComputeForSystem(SystemFamilySnapshot snapshot);

    /// <summary>
    /// Compute the ordered canonical sections of a loadable family
    /// snapshot (Issue #249, Phase 1). Concatenating the sections in
    /// order reproduces the canonical string behind
    /// <see cref="ComputeForLoadable"/> byte-for-byte. Sections are an
    /// analytics layer (per-section diff, change classification) — the
    /// single identity hash is unchanged. Returns <c>null</c> for a null
    /// snapshot.
    /// </summary>
    IReadOnlyList<ContentSectionHash>? ComputeSectionsForLoadable(FamilySnapshot snapshot);

    /// <summary>
    /// Compute the ordered canonical sections of a system family snapshot
    /// (Issue #249, Phase 1): the META prefix followed by per-type entries
    /// (VALUES, FAMKEY, STRUCT, ROUTING, SEGMENTS, SUBTYPES, RAILING,
    /// WIRE) carrying <see cref="ContentSectionHash.TypeName"/>.
    /// Concatenating the sections in order reproduces the canonical
    /// string behind <see cref="ComputeForSystem"/> byte-for-byte.
    /// Returns <c>null</c> for a null snapshot or a snapshot without types.
    /// </summary>
    IReadOnlyList<ContentSectionHash>? ComputeSectionsForSystem(SystemFamilySnapshot snapshot);

    /// <summary>
    /// Compute per-type content hashes of a loadable family (Issue #249,
    /// Phase 2): type name → SHA-256 of the type's canonical substring
    /// (escaped name + ordinal-sorted parameter values, exactly as
    /// embedded in the TYPES section). Per-type GEOMETRY is deliberately
    /// not measured (a regeneration per type is unacceptable for fittings
    /// with 200 types); baked-in parameter values determine the type's
    /// geometry (ADR-033). Keys compare ordinal-ignore-case.
    /// Returns <c>null</c> for a null snapshot.
    /// </summary>
    IReadOnlyDictionary<string, string>? ComputePerTypeHashesForLoadable(FamilySnapshot snapshot);

    /// <summary>
    /// Compute per-type content hashes of a system family (Issue #249,
    /// Phase 2; content-grade #179): one <see cref="SystemTypeContentHash"/>
    /// per type — SHA-256 of the type's full canonical body (name +
    /// values + FAMKEY + STRUCT + ROUTING + SEGMENTS + SUBTYPES + RAILING
    /// + WIRE). A list is returned (not a dictionary) because one snapshot
    /// can contain same-named types of different system families; key the
    /// entries via <see cref="SystemTypeContentHash.IdentityKey"/> when a
    /// lookup map is needed. Returns <c>null</c> for a null snapshot or a
    /// snapshot without types.
    /// </summary>
    IReadOnlyList<SystemTypeContentHash>? ComputePerTypeHashesForSystem(SystemFamilySnapshot snapshot);

    /// <summary>
    /// Diagnostics-only: the canonical string behind
    /// <see cref="ComputeForLoadable"/>. Used to localize a post-verify
    /// mismatch (first differing token + section context) in failure logs —
    /// never for hashing decisions. Returns <c>null</c> for a null snapshot.
    /// </summary>
    string? BuildLoadableCanonicalStringForDiagnostics(FamilySnapshot snapshot);
}
