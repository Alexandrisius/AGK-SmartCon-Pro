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
    /// </summary>
    FamilyContentHash? ComputeForLoadable(FamilySnapshot snapshot);

    /// <summary>
    /// #209: verification-grade hash for comparing an EMBEDDED
    /// nested family against its source .rfa after a reload (family-editor
    /// stale update). Same sections as <see cref="ComputeForLoadable"/> —
    /// STRICT on definitions (incl. parameter groups) — except metrics
    /// that are physically host-dependent, not content: regen-driven
    /// geometry (volumes, bounds, surface areas, curve lengths) and
    /// connector sizes/origins, which legitimately move when the host
    /// drives the nested family's instance parameters. Never stored in the
    /// catalog — identity (dedup/versioning) keeps using
    /// <see cref="ComputeForLoadable"/>.
    /// </summary>
    FamilyContentHash? ComputeForEmbeddedVerification(FamilySnapshot snapshot);

    /// <summary>
    /// Compute a content hash for a system family snapshot. Returns
    /// <c>null</c> if the snapshot is null or has no types.
    /// </summary>
    FamilyContentHash? ComputeForSystem(SystemFamilySnapshot snapshot);
}
