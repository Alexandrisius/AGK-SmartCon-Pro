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
public sealed record GeometryMetrics(
    int TotalFormCount,
    IReadOnlyList<FormMetrics> Forms);

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
