using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Synchronizes a MEP segment (pipe/duct segment with its size table) in the
/// active project with the same-named segment from the source mini-project
/// (Issue #104). Segments are matched by name; the size table is brought to
/// the reference: missing sizes are added, differing sizes are corrected,
/// extra sizes are removed only when unused by placed MEP curves (Revit
/// forbids deleting in-use content) — the residue is reported as
/// not-converged.
/// </summary>
/// <remarks>
/// Must be called on the Revit main thread (I-01) and inside an open
/// transaction. Material dependencies are synchronized through
/// <see cref="IMaterialSyncService"/>; missing pipe schedules are created
/// via <c>PipeScheduleType.Create</c>.
/// </remarks>
public interface ISegmentSyncService
{
    /// <summary>
    /// Read the reference data of a segment from the source mini-project.
    /// Returns <c>null</c> when the source has no segment with this name.
    /// </summary>
    SegmentSnapshot? ReadSegment(Document sourceDoc, string segmentName);

    /// <summary>
    /// Synchronize the segment <paramref name="segmentName"/> into the
    /// active project (create or update + size table convergence).
    /// </summary>
    SegmentSyncResult SyncSegment(Document sourceDoc, Document activeDoc, string segmentName);
}
