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
}
