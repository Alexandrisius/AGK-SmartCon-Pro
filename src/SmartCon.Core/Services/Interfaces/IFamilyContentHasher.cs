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
    /// STRICT on definitions (formulas, types/values, nested, facts, flags,
    /// connectors' classification) — except what no reload can physically
    /// transfer: regen-driven geometry (volumes, bounds, surface areas,
    /// curve lengths), connector sizes/origins, AND parameter groups
    /// (proven 2026-08-11: no merge — API or UI — ever propagates a
    /// parameter's group, so a group-included comparison is unreachable in
    /// principle; groups stay in the identity hash). Never stored in the
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
