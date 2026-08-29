namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One stored size row of a pipe segment of a catalog version (ADR-072,
/// Phase 3): the source of the routing editor's min/max size dropdowns —
/// strictly the segment's <c>NominalDiameter</c> values, the same list the
/// Revit routing dialog offers. Diameters are internal units (feet), the
/// storage form of <see cref="SegmentSizeSnapshot"/>. Written at system
/// import (SegmentSizeWriter), copied verbatim by the routing editor save,
/// backfilled for legacy versions by the segment-sizes-v1 actualization.
/// </summary>
public sealed record SegmentSizeRecord(
    string SegmentName,
    double NominalDiameter,
    double InnerDiameter,
    double OuterDiameter,
    bool UsedInSizeLists,
    bool UsedInSizing,
    int SortOrder);
