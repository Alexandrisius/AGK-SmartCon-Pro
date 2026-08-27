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
/// <param name="DimensionCount">Number of LABELED <c>Dimension</c> elements
/// not owned by a form sketch (FHV13). Unlabeled dimensions — including
/// Revit's automatic sketch dimensions — are not parameter wiring and are
/// not counted; 0 for families without labeled dimensions.</param>
/// <param name="TotalSymbolicCurveLength">Summed curve length of all
/// symbolic curves, internal units (feet). Catches 2D edits that keep
/// the element count constant (ADR-056).</param>
/// <param name="TotalDetailCurveLength">Summed length of detail
/// curves.</param>
/// <param name="TotalModelCurveLength">Summed length of model
/// curves.</param>
public sealed record GeometryMetrics(
    int TotalFormCount,
    IReadOnlyList<FormMetrics> Forms,
    int SymbolicCurveCount = 0,
    int DetailCurveCount = 0,
    int ModelCurveCount = 0,
    int TextNoteCount = 0,
    int ReferencePlaneCount = 0,
    int DimensionCount = 0,
    double TotalSymbolicCurveLength = 0,
    double TotalDetailCurveLength = 0,
    double TotalModelCurveLength = 0,
    /// <summary>
    /// FHV12 (#249, Phase 3): nested <c>FamilyInstance</c> placements
    /// inside the family document — symbol identity + quantized transform
    /// + visibility. Pre-FHV12 only the nested family NAMES were hashed
    /// (NESTED section): moving or rotating a nested part inside the
    /// family passed the content hash silently.
    /// </summary>
    IReadOnlyList<NestedInstanceSnapshot>? NestedInstances = null);

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
/// <param name="SurfaceArea">Total surface area of all faces across all
/// solids, in internal units (square feet). 0 when geometry could not
/// be extracted. Catches shape edits that preserve volume and face
/// count (ADR-056).</param>
/// <param name="Bounds">Form bounding box (view-independent), or
/// <c>null</c> when unavailable. Catches translations/proportion edits
/// that preserve volume (ADR-056).</param>
public sealed record FormMetrics(
    string FormKind,
    bool IsSolid,
    double Volume,
    int FaceCount,
    int EdgeCount,
    string? SubcategoryName,
    double SurfaceArea = 0,
    BoundingBoxSnapshot? Bounds = null,
    /// <summary>
    /// FHV12 (#249, Phase 3): volume-weighted centroid of the form's
    /// solids (<c>Solid.ComputeCentroid()</c>), hashed with 1e-4 ft
    /// rounding like connector origins. Catches translations that keep
    /// volume, face counts AND the axis-aligned bounds (e.g. a shape
    /// moved within its own bounding box).
    /// </summary>
    PointSnapshot? Centroid = null,
    /// <summary>
    /// FHV12: histogram of the form's face kinds (PlanarFace,
    /// CylindricalFace, ConicalFace, RevolvedFace, …) — face KINDS are
    /// stable across regenerations, unlike tessellation vertex counts
    /// (Autodesk forum). Sorted by kind ordinal.
    /// </summary>
    IReadOnlyList<FaceTypeCount>? FaceTypes = null,
    /// <summary>
    /// FHV12: summed edge lengths of all solids (internal units, feet) —
    /// stabler than the edge COUNT for shape edits that re-split edges.
    /// </summary>
    double TotalEdgeLength = 0,
    /// <summary>
    /// FHV12: the form's resolved display color (RGBA 0-255) through the
    /// same fallback chain the GLB preview uses (face/form material →
    /// element category → family category → owner). <c>null</c> when no
    /// level resolved a material — a deterministic state.
    /// </summary>
    MaterialColorSnapshot? MaterialColor = null,
    /// <summary>
    /// FHV12: the form's visibility flags — the raw
    /// <c>IS_VISIBLE_PARAM</c> value (the associable "Visible" parameter,
    /// per-type through its binding) and the Fine detail-level flag from
    /// <c>GenericForm.GetVisibility()</c>. A binding flip shifts the hash
    /// even when every metric stays identical.
    /// </summary>
    FormVisibilitySnapshot? Visibility = null);

/// <summary>A 3D point in internal units (feet), hashed with 1e-4 ft rounding.</summary>
public sealed record PointSnapshot(double X, double Y, double Z);

/// <summary>One face-kind counter of a <see cref="FormMetrics"/> histogram (FHV12).</summary>
public sealed record FaceTypeCount(string FaceKind, int Count);

/// <summary>Resolved display color (RGBA, 0-255 per channel) of a form (FHV12).</summary>
public sealed record MaterialColorSnapshot(int R, int G, int B, int A);

/// <summary>
/// Visibility flags of a family form (FHV12): the raw
/// <c>IS_VISIBLE_PARAM</c> value (<c>null</c> when unreadable) and the
/// Fine detail-level flag (<c>null</c> when the visibility object is
/// unavailable).
/// </summary>
public sealed record FormVisibilitySnapshot(int? IsVisibleParamValue, bool? IsShownInFine);

/// <summary>
/// One nested <c>FamilyInstance</c> placement inside a family document
/// (FHV12, #249 Phase 3): the symbol identity (family + symbol name —
/// user content, locale-stable), the placement transform quantized to
/// 1e-4 ft (origin + basis vectors), and the raw
/// <c>IS_VISIBLE_PARAM</c> value.
/// </summary>
public sealed record NestedInstanceSnapshot(
    string FamilyName,
    string SymbolName,
    double OriginX,
    double OriginY,
    double OriginZ,
    double BasisXx,
    double BasisXy,
    double BasisXz,
    double BasisYx,
    double BasisYy,
    double BasisYz,
    double BasisZx,
    double BasisZy,
    double BasisZz,
    int? IsVisibleParamValue);

/// <summary>
/// Axis-aligned bounding box in internal units (feet). Hashed with
/// 4-decimal rounding (1e-4 ft ≈ 0.03 mm) so microscopic regen noise
/// does not shift the content hash.
/// </summary>
public sealed record BoundingBoxSnapshot(
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ);
