namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Aggregated geometry metrics for a loadable family document. Used as
/// part of the content fingerprint so that adding/removing a form, or
/// changing an extrusion depth, shifts the hash.
/// </summary>
/// <param name="TotalFormCount">Total number of <c>GenericForm</c>
/// elements (extrusions, sweeps, revolutions, blends, swept blends,
/// free-form) found in the family document.</param>
/// <param name="Forms">Per-form metrics, sorted by
/// (FormKind, IsSolid, Volume) for deterministic output.</param>
/// <param name="SymbolicCurveCount">Number of <c>SymbolicCurve</c>
/// elements (2D symbolic lines in annotation/title-block families).
/// 0 for 3D-only families.</param>
/// <param name="DetailCurveCount">Number of <c>DetailCurve</c> elements
/// (2D detail lines). 0 for 3D-only families.</param>
/// <param name="ModelCurveCount">Number of <c>ModelCurve</c> elements
/// (2D model lines, used in some annotation families). 0 for 3D-only
/// families.</param>
/// <param name="TextNoteCount">Number of <c>TextNote</c> elements
/// (text labels in annotation/title-block families). 0 for 3D-only
/// families.</param>
/// <param name="ReferencePlaneCount">Number of <c>ReferencePlane</c>
/// elements. 0 for families without reference planes.</param>
/// <param name="DimensionCount">Number of <c>Dimension</c> elements
/// in the family document. 0 for families without dimensions.</param>
public sealed record GeometryMetrics(
    int TotalFormCount,
    IReadOnlyList<FormMetrics> Forms,
    int SymbolicCurveCount = 0,
    int DetailCurveCount = 0,
    int ModelCurveCount = 0,
    int TextNoteCount = 0,
    int ReferencePlaneCount = 0,
    int DimensionCount = 0);

/// <summary>
/// Metrics for a single <c>GenericForm</c> element inside a family
/// document.
/// </summary>
/// <param name="FormKind">Class name: <c>"Extrusion"</c>,
/// <c>"Sweep"</c>, <c>"Revolution"</c>, <c>"Blend"</c>,
/// <c>"SweptBlend"</c>, or <c>"GenericForm"</c> for free-form/other.</param>
/// <param name="IsSolid"><c>true</c> for solid forms, <c>false</c> for
/// void forms.</param>
/// <param name="Volume">Total volume of all solids in this form, in
/// Revit internal units (cubic feet). High precision (6 decimal places)
/// so a 1 mm change in extrusion depth shifts the value.</param>
/// <param name="FaceCount">Total number of faces across all solids, or
/// 0 if geometry could not be extracted (known bug for shared nested
/// families).</param>
/// <param name="EdgeCount">Total number of edges across all solids, or
/// 0 if geometry could not be extracted.</param>
/// <param name="SubcategoryName">Subcategory name assigned to the form,
/// or <c>null</c> if the form uses the family category.</param>
public sealed record FormMetrics(
    string FormKind,
    bool IsSolid,
    double Volume,
    int FaceCount,
    int EdgeCount,
    string? SubcategoryName);
