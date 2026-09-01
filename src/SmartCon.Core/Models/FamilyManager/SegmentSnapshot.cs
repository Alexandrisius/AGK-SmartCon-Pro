namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Reference data of a MEP segment (pipe/duct segment) read from the catalog
/// mini-project (Issue #104). A segment = name + material + schedule +
/// size table. Used by the segment synchronizer to bring the project's
/// segment to the catalog reference: add/update always, remove only unused
/// sizes.
/// </summary>
/// <param name="Name">Segment name (document content).</param>
/// <param name="MaterialName">Name of the segment's material, or
/// <c>null</c> when unresolved.</param>
/// <param name="ScheduleName">Name of the <c>PipeScheduleType</c> for pipe
/// segments; <c>null</c> for duct segments.</param>
/// <param name="Roughness">Segment roughness (internal units).</param>
/// <param name="Sizes">Size table in segment order.</param>
/// <param name="RuleMinSizeFeet">FHV21 (owner decision 2026-09-01): the
/// size-range criterion of the routing RULE that references this segment
/// (Мин in Revit's routing dialog, feet). <c>null</c> = unrestricted or
/// the snapshot was built outside a rule context (segment sync reads the
/// segment entity only — the range is rule data, not segment data).</param>
/// <param name="RuleMaxSizeFeet">Макс of the rule's criterion (feet);
/// <c>null</c> = unrestricted.</param>
public sealed record SegmentSnapshot(
    string Name,
    string? MaterialName,
    string? ScheduleName,
    double Roughness,
    IReadOnlyList<SegmentSizeSnapshot> Sizes,
    double? RuleMinSizeFeet = null,
    double? RuleMaxSizeFeet = null);

/// <summary>
/// One size row of a segment's size table. All diameters are in internal
/// units (feet). Mirrors the <c>MEPSize</c> constructor shape.
/// </summary>
public sealed record SegmentSizeSnapshot(
    double NominalDiameter,
    double InnerDiameter,
    double OuterDiameter,
    bool UsedInSizeLists,
    bool UsedInSizing);
