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
    /// <summary>
    /// #250: aggregate CONTENT metrics of a nested <see cref="FamilyInstance"/>'s
    /// SYMBOL geometry (placement-invariant — read from
    /// <c>GetSymbolGeometry()</c>) for the VIEW3D hash: a nested child's
    /// geometry/material edit must re-key the per-type CAS pool even when
    /// the placement (name + transform) is untouched. Best-effort:
    /// <c>null</c> on any read failure (the hasher emits a deterministic
    /// marker then). Face colors use the face material only — the nested
    /// child has no form-level fallback chain in the host context.
    /// </summary>
    internal static FormMetrics? ComputeNestedContentMetrics(
        Document familyDoc, FamilyInstance inst, Options options)
    {
        try
        {
            var geomElem = inst.get_Geometry(options);
            if (geomElem is null)
            {
                return null;
            }

            double volume = 0, surfaceArea = 0, totalEdgeLength = 0;
            double centroidX = 0, centroidY = 0, centroidZ = 0;
            int faceCount = 0, edgeCount = 0;
            var faceTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var faceColors = new Dictionary<MaterialColorSnapshot, int>();
            var faceColorCache = new Dictionary<ElementId, MaterialColorSnapshot?>();

            foreach (var geomObj in geomElem)
            {
                if (geomObj is GeometryInstance geomInst)
                {
                    var symbolGeom = geomInst.GetSymbolGeometry();
                    if (symbolGeom is null)
                    {
                        continue;
                    }
                    foreach (var innerObj in symbolGeom)
                    {
                        if (innerObj is Solid solid && solid.Volume > 0)
                        {
                            AccumulateSolid(solid, ref volume, ref surfaceArea, ref faceCount, ref edgeCount,
                                ref totalEdgeLength, ref centroidX, ref centroidY, ref centroidZ, faceTypes,
                                familyDoc, null, faceColors, faceColorCache);
                        }
                    }
                }
                else if (geomObj is Solid solid && solid.Volume > 0)
                {
                    AccumulateSolid(solid, ref volume, ref surfaceArea, ref faceCount, ref edgeCount,
                        ref totalEdgeLength, ref centroidX, ref centroidY, ref centroidZ, faceTypes,
                        familyDoc, null, faceColors, faceColorCache);
                }
            }

            return new FormMetrics(
                FormKind: "NestedContent",
                IsSolid: true,
                Volume: volume,
                FaceCount: faceCount,
                EdgeCount: edgeCount,
                SubcategoryName: null,
                SurfaceArea: surfaceArea,
                Bounds: null,
                Centroid: volume > 0
                    ? new PointSnapshot(centroidX / volume, centroidY / volume, centroidZ / volume)
                    : null,
                FaceTypes: faceTypes.Count > 0
                    ? faceTypes.Select(kv => new FaceTypeCount(kv.Key, kv.Value)).ToList()
                    : null,
                TotalEdgeLength: totalEdgeLength,
                MaterialColor: null,
                Visibility: null,
                FaceColors: faceColors.Count > 0
                    ? faceColors.Select(kv => new FaceColorCount(kv.Key, kv.Value)).ToList()
                    : null);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Nested content metrics failed for '{inst.Symbol?.Family?.Name}/{inst.Symbol?.Name}': {ex.Message}");
            return null;
        }
    }

    private static void AccumulateSolid(
        Solid solid, ref double volume, ref double surfaceArea, ref int faceCount, ref int edgeCount,
        ref double totalEdgeLength,
        ref double centroidX, ref double centroidY, ref double centroidZ,
        Dictionary<string, int> faceTypes,
        Document familyDoc,
        MaterialColorSnapshot? formColor,
        Dictionary<MaterialColorSnapshot, int> faceColors,
        Dictionary<ElementId, MaterialColorSnapshot?> faceColorCache)
    {
        volume += solid.Volume;
        faceCount += solid.Faces.Size;
        edgeCount += solid.Edges.Size;
        try
        {
            foreach (Face face in solid.Faces)
            {
                surfaceArea += face.Area;
                // FHV12: face-kind histogram — kinds are stable across
                // regenerations, unlike tessellation vertex counts.
                var kind = face.GetType().Name;
                faceTypes.TryGetValue(kind, out var count);
                faceTypes[kind] = count + 1;

                // FHV18 (#251): per-face resolved color — the face's own
                // material wins (a face PAINT overrides the form color in
                // the GLB too, #108); an own material without a resolvable
                // color and a face without any material land in the
                // form-level bucket. Faces with no color anywhere are not
                // counted (a deterministic state — FaceCount covers them).
                var bucket = formColor;
                var faceMaterialId = face.MaterialElementId;
                if (faceMaterialId is not null && faceMaterialId != ElementId.InvalidElementId)
                {
                    if (!faceColorCache.TryGetValue(faceMaterialId, out var faceColor))
                    {
                        faceColor = TryGetMaterialColorById(familyDoc, faceMaterialId);
                        faceColorCache[faceMaterialId] = faceColor;
                    }
                    if (faceColor is not null)
                    {
                        bucket = faceColor;
                    }
                }
                if (bucket is not null)
                {
                    faceColors.TryGetValue(bucket, out var colorCount);
                    faceColors[bucket] = colorCount + 1;
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Face area accumulation failed: {ex.Message}");
        }
        try
        {
            foreach (Edge edge in solid.Edges)
            {
                var curve = edge.AsCurve();
                if (curve is not null)
                {
                    totalEdgeLength += curve.Length;
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Edge length accumulation failed: {ex.Message}");
        }
        try
        {
            // FHV12: volume-weighted centroid accumulation.
            var c = solid.ComputeCentroid();
            centroidX += c.X * solid.Volume;
            centroidY += c.Y * solid.Volume;
            centroidZ += c.Z * solid.Volume;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Centroid accumulation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Nested family names split by the shared flag (ADR-056): shared
    /// nested families keep their long-standing hash role, non-shared
    /// ones join in FHV3 (their replacement is real content too).
    /// Single collector pass for both lists.
    /// </summary>
    private static (List<string> Shared, List<string> NonShared) ExtractNestedNames(Document familyDoc)
    {
        var seenShared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNonShared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shared = new List<string>();
        var nonShared = new List<string>();

        try
        {
            var collector = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(FamilyInstance));

            foreach (FamilyInstance fi in collector)
            {
                var family = fi.Symbol?.Family;
                if (family is null) continue;

                var name = family.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (IsSharedFamily(family))
                {
                    if (seenShared.Add(name))
                        shared.Add(name);
                }
                else
                {
                    if (seenNonShared.Add(name))
                        nonShared.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Nested family scan failed: {ex.Message}");
        }

        return (
            shared.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            nonShared.OrderBy(n => n, StringComparer.Ordinal).ToList());
    }

    private static bool IsSharedFamily(Autodesk.Revit.DB.Family family)
    {
        try
        {
            var p = family.get_Parameter(BuiltInParameter.FAMILY_SHARED);
            return p is not null && p.AsInteger() == 1;
        }
        catch
        {
            return false;
        }
    }
}
