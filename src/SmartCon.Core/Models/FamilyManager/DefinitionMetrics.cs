namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Type-independent DEFINITION wiring of a loadable family (FHV12, Issue
/// #249, Phase 3): the constraint graph that turns parameter values into
/// geometry. Conceptually <c>Geometry(type) = F(Definitions + type values
/// + formulas + lookup)</c>, so this section closes the pre-FHV12 blind
/// spots that no metric on the default type could see:
/// <list type="bullet">
/// <item>re-binding a form's visibility/material to a DIFFERENT family
/// parameter with the same current values;</item>
/// <item>re-binding a dimension label to a different family parameter
/// (the dimension COUNT does not change, GEOM2D could not see it);</item>
/// <item>extrusion start/end offset label associations;</item>
/// <item>reference-plane identity (name) and Defines Origin flags.</item>
/// </list>
/// One collector pass, zero regenerations.
/// </summary>
public sealed record DefinitionMetrics(
    IReadOnlyList<FormDefinitionSnapshot> Forms,
    IReadOnlyList<DimensionDefinitionSnapshot> Dimensions,
    IReadOnlyList<ReferencePlaneDefinitionSnapshot> ReferencePlanes);

/// <summary>
/// Definition-level wiring of one <c>GenericForm</c>: which FAMILY
/// parameters drive its visibility / material / extrusion offsets.
/// <c>null</c> = not associated (a constant or nothing). Associated
/// names are user content (locale-stable). Offsets are internal units
/// (feet), <c>null</c> for non-extrusion forms.
/// </summary>
public sealed record FormDefinitionSnapshot(
    string FormKind,
    bool IsSolid,
    string? SubcategoryName,
    string? VisibilityParameterName,
    string? MaterialParameterName,
    string? ExtrusionStartParameterName,
    string? ExtrusionEndParameterName,
    double? ExtrusionStartOffset,
    double? ExtrusionEndOffset);

/// <summary>
/// Definition-level wiring of one <c>Dimension</c>: the associated family
/// parameter label (<c>null</c> = unlabeled), the dimension style name
/// (user content) and the segment count.
/// </summary>
public sealed record DimensionDefinitionSnapshot(
    string? LabelParameterName,
    string StyleName,
    int SegmentCount);

/// <summary>
/// Definition-level identity of one <c>ReferencePlane</c>: its name
/// (user content; empty when unnamed) and the Defines Origin flag
/// (<c>null</c> when unreadable).
/// </summary>
public sealed record ReferencePlaneDefinitionSnapshot(
    string Name,
    bool? DefinesOrigin);
