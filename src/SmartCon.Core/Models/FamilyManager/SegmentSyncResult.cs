using Autodesk.Revit.DB;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of synchronizing one MEP segment into the project (Issue #104).
/// </summary>
/// <param name="SegmentId">Resolved segment element id in the project, or
/// <c>null</c> when the segment could not be found/created.</param>
/// <param name="SizesAdded">Sizes added or re-created with corrected
/// inner/outer diameters.</param>
/// <param name="SizesRemoved">Unused sizes removed to match the reference.</param>
/// <param name="SizesNotConverged">Reference-missing sizes that could not be
/// removed (in use by placed MEP curves or the last remaining size) plus
/// used sizes whose dimensions could not be corrected. Surfaced to the user
/// as the "not converged to reference" counter.</param>
public sealed record SegmentSyncResult(
    ElementId? SegmentId,
    int SizesAdded,
    int SizesRemoved,
    int SizesNotConverged);
