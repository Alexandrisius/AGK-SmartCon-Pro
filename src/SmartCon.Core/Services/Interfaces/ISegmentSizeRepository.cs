using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Segment size tables of catalog versions (ADR-072, Phase 3): the routing
/// editor offers the stored <c>NominalDiameter</c> values as the min/max
/// rule criteria, mirroring the Revit routing dialog's size dropdowns.
/// Rows live per catalog version (cascade-deleted with it).
/// </summary>
public interface ISegmentSizeRepository
{
    /// <summary>DELETE+INSERT of one version's size rows (import/backfill).</summary>
    Task ReplaceForVersionAsync(
        string catalogVersionId, IReadOnlyList<SegmentSizeRecord> sizes, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="ReplaceForVersionAsync"/> but resolves the item's
    /// current version (import path). No current version = warn + no-op.
    /// </summary>
    Task ReplaceForCurrentVersionAsync(
        string catalogItemId, IReadOnlyList<SegmentSizeRecord> sizes, CancellationToken ct = default);

    /// <summary>All size rows of a version, ordered by segment + size.</summary>
    Task<IReadOnlyList<SegmentSizeRecord>> ReadForVersionAsync(
        string catalogVersionId, CancellationToken ct = default);

    /// <summary>
    /// Distinct nominal diameters (feet, ascending) across all segments of
    /// the version — the editor dropdown source. Empty = no segments
    /// (ducts/conduit/tray) — the editor hides the size pickers.
    /// </summary>
    Task<IReadOnlyList<double>> ReadDistinctNominalsAsync(
        string catalogVersionId, CancellationToken ct = default);
}
