using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilySnapshotExtractor
{
    private static GeometryMetrics ExtractGeometry(Document familyDoc)
    {
        try
        {
            var forms = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(GenericForm))
                .Cast<GenericForm>()
                .ToList();

            // FHV13/14 (#249 follow-up): curves dependent on a form (its
            // sketch content) are the parametric skeleton of 3D forms —
            // already measured by the GEOM metrics — so GEOM2D counts only
            // FREE 2D content. Without the exclusion, every "added a 3D
            // body" edit fired the 2D section (sketch curves + Revit's
            // automatic sketch dimensions).
            var sketchOwnedIds = CollectFormOwnedCurveIds(forms);

            var (symbolicCount, symbolicLength) = CountAndMeasureCurves(familyDoc,
                new CurveElementFilter(CurveElementType.SymbolicCurve), sketchOwnedIds);
            var (detailCount, detailLength) = CountAndMeasureCurves(familyDoc,
                new CurveElementFilter(CurveElementType.DetailCurve), sketchOwnedIds);
            var (modelCount, modelLength) = CountAndMeasureCurves(familyDoc,
                new CurveElementFilter(CurveElementType.ModelCurve), sketchOwnedIds);
            var textNoteCount = CountElements(familyDoc, typeof(TextNote));
            var refPlaneCount = CountElements(familyDoc, typeof(ReferencePlane));
            var dimensionCount = CountLabeledDimensions(familyDoc, sketchOwnedIds);

            // FHV12 (#249, Phase 3): nested instance placements are content
            // even in a form-less family (a pure container family).
            var nestedInstances = ExtractNestedInstances(familyDoc);

            if (forms.Count == 0)
            {
                SmartConLogger.Debug(
                    $"No GenericForm elements found in family document. " +
                    $"2D: symbolic={symbolicCount}, detail={detailCount}, model={modelCount}, " +
                    $"text={textNoteCount}, refPlane={refPlaneCount}, dim={dimensionCount}");
                return new GeometryMetrics(
                    0, Array.Empty<FormMetrics>(),
                    symbolicCount, detailCount, modelCount,
                    textNoteCount, refPlaneCount, dimensionCount,
                    symbolicLength, detailLength, modelLength,
                    nestedInstances.Count > 0 ? nestedInstances : null);
            }

            // FHV12 (#249, Phase 3): IncludeNonVisibleObjects = true —
            // conditionally visible solids (IS_VISIBLE_PARAM = 0 on the
            // current type) enter the metrics WITH their visibility flag
            // recorded, closing the blind spot of conditional forms
            // invisible on the default type. Aligned with the GLB
            // extractor's options. NOTE: forms are NOT filtered by
            // form.Visible — probe 2026-08-27 showed form.Visible tracks
            // IS_VISIBLE_PARAM (both are the same "Visible" flag), so
            // filtering would drop exactly the conditional forms this
            // change exists to capture. FHV11 already counted invisible
            // forms (with zeroed metrics); FHV12 measures them.
            var options = new Options
            {
                ComputeReferences = false,
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = true
            };

            var metricsList = new List<FormMetrics>(forms.Count);

            foreach (var form in forms)
            {
                var metric = ExtractFormMetrics(form, options, familyDoc);
                metricsList.Add(metric);
            }

            var sortedMetrics = metricsList
                .OrderBy(f => f.FormKind, StringComparer.Ordinal)
                .ThenBy(f => f.IsSolid)
                .ThenBy(f => f.Volume)
                .ToList();

            SmartConLogger.Debug(
                $"Geometry: {forms.Count} forms ({metricsList.Count} visible in editor), " +
                $"{sortedMetrics.Count(f => f.IsSolid)} solid, " +
                $"{sortedMetrics.Count(f => !f.IsSolid)} void, " +
                $"{nestedInstances.Count} nested instances. " +
                $"2D: symbolic={symbolicCount}, detail={detailCount}, model={modelCount}, " +
                $"text={textNoteCount}, refPlane={refPlaneCount}, dim={dimensionCount}");

            return new GeometryMetrics(
                forms.Count, sortedMetrics,
                symbolicCount, detailCount, modelCount,
                textNoteCount, refPlaneCount, dimensionCount,
                symbolicLength, detailLength, modelLength,
                nestedInstances.Count > 0 ? nestedInstances : null);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Geometry extraction failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: hash will use 0 forms — check family document for corruption]");
            return new GeometryMetrics(0, Array.Empty<FormMetrics>());
        }
    }

    /// <summary>
    /// FHV12 (#249, Phase 3): nested <c>FamilyInstance</c> placements —
    /// symbol identity + transform + visibility flag. Pre-FHV12 only the
    /// nested family names participated in the hash (NESTED section):
    /// moving or rotating a nested part passed silently.
    /// </summary>
    private static List<NestedInstanceSnapshot> ExtractNestedInstances(Document familyDoc)
    {
        var result = new List<NestedInstanceSnapshot>();
        try
        {
            var collector = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(FamilyInstance));
            foreach (FamilyInstance fi in collector)
            {
                try
                {
                    var familyName = fi.Symbol?.Family?.Name;
                    if (string.IsNullOrEmpty(familyName))
                    {
                        continue;
                    }
                    var symbolName = fi.Symbol?.Name ?? string.Empty;
                    var transform = fi.GetTransform();
                    int? isVisibleParam = null;
                    try
                    {
                        isVisibleParam = fi.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM)?.AsInteger();
                    }
                    catch
                    {
                        // flag unreadable — null recorded
                    }

                    result.Add(new NestedInstanceSnapshot(
                        familyName!, symbolName,
                        transform.Origin.X, transform.Origin.Y, transform.Origin.Z,
                        transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z,
                        transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z,
                        transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z,
                        isVisibleParam));
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"Nested instance read failed (Id={fi.Id}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Nested instance collection failed: {ex.Message}");
        }
        return result;
    }

    internal static FormMetrics ExtractFormMetrics(GenericForm form, Options options, Document familyDoc)
    {
        var formKind = form.GetType().Name;
        var isSolid = form.IsSolid;
        double volume = 0;
        double surfaceArea = 0;
        int faceCount = 0;
        int edgeCount = 0;
        double totalEdgeLength = 0;
        double centroidX = 0, centroidY = 0, centroidZ = 0;
        var faceTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        string? subcategoryName = null;
        BoundingBoxSnapshot? bounds = null;

        // FHV18 (#251): per-face resolved-color histogram inputs — the
        // form-level fallback color resolved ONCE (the same chain the GLB
        // side uses), plus a per-material color cache so a 1000-face form
        // costs one lookup per material, not per face.
        var formColor = ResolveFormMaterialColor(form, familyDoc);
        var faceColors = new Dictionary<MaterialColorSnapshot, int>();
        var faceColorCache = new Dictionary<ElementId, MaterialColorSnapshot?>();

        try
        {
            var geomElem = form.get_Geometry(options);
            if (geomElem is not null)
            {
                foreach (var geomObj in geomElem)
                {
                    if (geomObj is Solid solid && solid.Volume > 0)
                    {
                        AccumulateSolid(solid, ref volume, ref surfaceArea, ref faceCount, ref edgeCount,
                            ref totalEdgeLength, ref centroidX, ref centroidY, ref centroidZ, faceTypes,
                            familyDoc, formColor, faceColors, faceColorCache);
                    }
                    else if (geomObj is GeometryInstance geomInst)
                    {
                        var transformedGeom = geomInst.GetInstanceGeometry();
                        if (transformedGeom is not null)
                        {
                            foreach (var innerObj in transformedGeom)
                            {
                                if (innerObj is Solid innerSolid && innerSolid.Volume > 0)
                                {
                                    AccumulateSolid(innerSolid, ref volume, ref surfaceArea, ref faceCount, ref edgeCount,
                                        ref totalEdgeLength, ref centroidX, ref centroidY, ref centroidZ, faceTypes,
                                        familyDoc, formColor, faceColors, faceColorCache);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Geometry read failed for form '{formKind}' (Id={form.Id}): {ex.Message}");
        }

        try
        {
            var bbox = form.get_BoundingBox(null);
            if (bbox is not null)
            {
                bounds = new BoundingBoxSnapshot(
                    bbox.Min.X, bbox.Min.Y, bbox.Min.Z,
                    bbox.Max.X, bbox.Max.Y, bbox.Max.Z);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Bounding box read failed for form '{formKind}' (Id={form.Id}): {ex.Message}");
        }

        try
        {
            subcategoryName = form.Subcategory?.Name;
        }
        catch
        {
            subcategoryName = null;
        }

        // FHV12 (#249, Phase 3): volume-weighted centroid — null when the
        // form produced no measurable solid (a deterministic state).
        PointSnapshot? centroid = volume > 0
            ? new PointSnapshot(centroidX / volume, centroidY / volume, centroidZ / volume)
            : null;

        return new FormMetrics(
            FormKind: formKind,
            IsSolid: isSolid,
            Volume: volume,
            FaceCount: faceCount,
            EdgeCount: edgeCount,
            SubcategoryName: subcategoryName,
            SurfaceArea: surfaceArea,
            Bounds: bounds,
            Centroid: centroid,
            FaceTypes: faceTypes.Count > 0
                ? faceTypes.Select(kv => new FaceTypeCount(kv.Key, kv.Value)).ToList()
                : null,
            TotalEdgeLength: totalEdgeLength,
            MaterialColor: formColor,
            Visibility: ExtractFormVisibility(form),
            FaceColors: faceColors.Count > 0
                ? faceColors.Select(kv => new FaceColorCount(kv.Key, kv.Value)).ToList()
                : null);
    }

    /// <summary>
    /// FHV12 (#249, Phase 3): resolved display color of a form through a
    /// fallback chain mirroring the GLB extractor
    /// (<c>RevitFamilyGeometryExtractor.GetColorForMaterialId</c>):
    /// form material parameter → element category material → owner family
    /// category material → <c>null</c> (deterministic "no color" state —
    /// the GLB side falls back to a constant grey, but for the HASH a
    /// null marker is the more honest, equally deterministic state).
    /// </summary>
    private static MaterialColorSnapshot? ResolveFormMaterialColor(GenericForm form, Document familyDoc)
    {
        try
        {
            var materialId = form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.AsElementId();
            var color = TryGetMaterialColorById(familyDoc, materialId);
            if (color is not null)
            {
                return color;
            }
        }
        catch
        {
            // fall through to the category chain
        }

        try
        {
            var color = TryGetMaterialColorById(familyDoc, form.Category?.Material?.Id);
            if (color is not null)
            {
                return color;
            }
        }
        catch
        {
            // fall through to the owner category
        }

        try
        {
            return TryGetMaterialColorById(familyDoc, familyDoc.OwnerFamily?.FamilyCategory?.Material?.Id);
        }
        catch
        {
            return null;
        }
    }

    private static MaterialColorSnapshot? TryGetMaterialColorById(Document doc, ElementId? materialId)
    {
        if (materialId is null)
        {
            return null;
        }
        try
        {
            if (doc.GetElement(materialId) is Material material)
            {
                var c = material.Color;
                if (c is not null && c.IsValid)
                {
                    return new MaterialColorSnapshot(c.Red, c.Green, c.Blue, 255);
                }
            }
        }
        catch
        {
            // unresolved level — caller walks the fallback chain
        }
        return null;
    }

    /// <summary>
    /// FHV12 (#249, Phase 3): visibility flags of a form — the raw
    /// <c>IS_VISIBLE_PARAM</c> value (the associable "Visible" parameter)
    /// and the Fine detail-level flag (<c>GenericForm.GetVisibility()</c>).
    /// Best-effort per flag: an unreadable flag records null.
    /// </summary>
    private static FormVisibilitySnapshot ExtractFormVisibility(GenericForm form)
    {
        int? isVisibleParam = null;
        try
        {
            isVisibleParam = form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM)?.AsInteger();
        }
        catch
        {
            // flag unreadable — null recorded
        }

        bool? isShownInFine = null;
        try
        {
            isShownInFine = form.GetVisibility()?.IsShownInFine;
        }
        catch
        {
            // visibility object unavailable — null recorded
        }

        return new FormVisibilitySnapshot(isVisibleParam, isShownInFine);
    }
}
