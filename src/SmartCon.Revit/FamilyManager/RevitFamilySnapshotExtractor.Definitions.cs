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
    /// FHV12 (#249, Phase 3): DEF — the type-independent definition
    /// wiring of the family: which FAMILY parameters drive each form's
    /// visibility/material/extrusion offsets, each dimension's label and
    /// each reference plane's identity. One collector pass per element
    /// class, zero regenerations. Bindings are read via
    /// <c>FamilyManager.GetAssociatedFamilyParameter</c> — the same API
    /// <c>AssociateElementParameterToFamilyParameter</c> writes through,
    /// so a re-binding to a different parameter with identical current
    /// values is caught here (the pre-FHV12 blind spot).
    /// </summary>
    private static DefinitionMetrics ExtractDefinitions(Document familyDoc)
    {
        var fm = familyDoc.FamilyManager;
        var forms = new List<FormDefinitionSnapshot>();
        var dimensions = new List<DimensionDefinitionSnapshot>();
        var planes = new List<ReferencePlaneDefinitionSnapshot>();

        try
        {
            foreach (var form in new FilteredElementCollector(familyDoc)
                .OfClass(typeof(GenericForm)).Cast<GenericForm>())
            {
                string? subcategory;
                try
                {
                    subcategory = form.Subcategory?.Name;
                }
                catch
                {
                    subcategory = null;
                }

                double? startOffset = null;
                double? endOffset = null;
                string? startBinding = null;
                string? endBinding = null;
                if (form is Extrusion extrusion)
                {
                    startBinding = GetParameterBindingName(fm, form, BuiltInParameter.EXTRUSION_START_PARAM);
                    endBinding = GetParameterBindingName(fm, form, BuiltInParameter.EXTRUSION_END_PARAM);
                    try
                    {
                        startOffset = extrusion.StartOffset;
                        endOffset = extrusion.EndOffset;
                    }
                    catch
                    {
                        // offsets unreadable — null recorded
                    }
                }

                forms.Add(new FormDefinitionSnapshot(
                    form.GetType().Name,
                    form.IsSolid,
                    subcategory,
                    GetParameterBindingName(fm, form, BuiltInParameter.IS_VISIBLE_PARAM),
                    GetParameterBindingName(fm, form, BuiltInParameter.MATERIAL_ID_PARAM),
                    startBinding,
                    endBinding,
                    startOffset,
                    endOffset));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Definition extraction (forms) failed: {ex.Message}");
        }

        try
        {
            foreach (var dim in new FilteredElementCollector(familyDoc)
                .OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                string? label = null;
                try
                {
                    label = dim.FamilyLabel?.Definition?.Name;
                }
                catch
                {
                    // unlabeled or unreadable — null recorded
                }

                string styleName;
                try
                {
                    styleName = familyDoc.GetElement(dim.GetTypeId())?.Name ?? string.Empty;
                }
                catch
                {
                    styleName = string.Empty;
                }

                var segmentCount = 0;
                try
                {
                    segmentCount = dim.Segments?.Size ?? 0;
                }
                catch
                {
                    // segments unreadable — 0 recorded
                }

                dimensions.Add(new DimensionDefinitionSnapshot(label, styleName, segmentCount));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Definition extraction (dimensions) failed: {ex.Message}");
        }

        try
        {
            foreach (var plane in new FilteredElementCollector(familyDoc)
                .OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
            {
                string name;
                try
                {
                    // ReferencePlane.Name throws for unnamed planes — fall
                    // back to the generic element name, then to empty.
                    name = plane.Name ?? string.Empty;
                }
                catch
                {
                    name = string.Empty;
                }

                bool? definesOrigin = null;
                try
                {
                    var value = plane.get_Parameter(BuiltInParameter.DATUM_PLANE_DEFINES_ORIGIN)?.AsInteger();
                    if (value.HasValue)
                    {
                        definesOrigin = value.Value != 0;
                    }
                }
                catch
                {
                    // flag unreadable — null recorded
                }

                planes.Add(new ReferencePlaneDefinitionSnapshot(name, definesOrigin));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Definition extraction (reference planes) failed: {ex.Message}");
        }

        return new DefinitionMetrics(forms, dimensions, planes);
    }

    /// <summary>
    /// Name of the FAMILY parameter associated with the element's
    /// built-in parameter, or <c>null</c> when the parameter is missing
    /// or not associated. Best-effort:
    /// <c>GetAssociatedFamilyParameter</c> may reject non-associable
    /// parameters — the binding then records null.
    /// </summary>
    private static string? GetParameterBindingName(Autodesk.Revit.DB.FamilyManager fm, Element element, BuiltInParameter bip)
    {
        try
        {
            var parameter = element.get_Parameter(bip);
            if (parameter is null)
            {
                return null;
            }
            return fm.GetAssociatedFamilyParameter(parameter)?.Definition?.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// FHV14 (#249 follow-up): element ids of curve elements DEPENDENT on a
    /// form — i.e. its sketch's model curves — via
    /// <c>Element.GetDependentElements</c> (Revit 2018+, every supported
    /// version; <c>Sketch.OwnerId</c>/<c>GetAllElements</c> are 2024+ and
    /// failed the net48 build). Dependent = "deleted together with the
    /// form", so free model/symbolic lines — which probe 2026-08-28 showed
    /// wrapped in their OWN sketches with no form owner — are never
    /// matched and stay counted. Best-effort per form: an unreadable form
    /// keeps its curves counted (fail-open, pre-FHV13 behaviour).
    /// </summary>
    private static HashSet<ElementId> CollectFormOwnedCurveIds(IReadOnlyList<GenericForm> forms)
    {
        var ids = new HashSet<ElementId>();
        try
        {
            var classFilter = new ElementClassFilter(typeof(CurveElement));
            foreach (var form in forms)
            {
                try
                {
                    foreach (var id in form.GetDependentElements(classFilter))
                    {
                        ids.Add(id);
                    }
                }
                catch
                {
                    // single form unreadable — its curves stay counted
                }
            }
        }
        catch
        {
            // dependency query unsupported — fall back to counting everything
        }
        return ids;
    }

    /// <summary>
    /// Count curve elements matching the filter and sum their geometry
    /// curve lengths (ADR-056). Length catches 2D edits that keep the
    /// element count constant (redrawn line of the same kind). Elements
    /// owned by form sketches (FHV13) are excluded — they are 3D-form
    /// wiring, measured by the GEOM metrics.
    /// </summary>
    private static (int Count, double TotalLength) CountAndMeasureCurves(
        Document doc, ElementFilter filter, ISet<ElementId>? excludeIds = null)
    {
        try
        {
            var count = 0;
            double length = 0;
            foreach (var element in new FilteredElementCollector(doc).WherePasses(filter))
            {
                if (excludeIds is not null && excludeIds.Contains(element.Id))
                {
                    continue;
                }
                count++;
                try
                {
                    if (element is CurveElement { GeometryCurve: not null } curveElement)
                        length += curveElement.GeometryCurve.Length;
                }
                catch
                {
                    // single curve unreadable — count stays, length skips it
                }
            }
            return (count, length);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// FHV13 (#249 follow-up): number of LABELED dimensions not owned by a
    /// form sketch. Unlabeled dimensions — including Revit's automatic
    /// sketch dimensions, which even API-created extrusions leave behind —
    /// are not parameter wiring: their geometric effect is measured by the
    /// GEOM metrics, and counting them fired GEOM2D on every 3D edit.
    /// </summary>
    private static int CountLabeledDimensions(Document doc, ISet<ElementId> excludeIds)
    {
        var count = 0;
        try
        {
            foreach (var dim in new FilteredElementCollector(doc)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>())
            {
                if (excludeIds.Contains(dim.Id))
                {
                    continue;
                }
                bool isLabeled;
                try { isLabeled = dim.FamilyLabel is not null; }
                catch { isLabeled = false; }
                if (isLabeled)
                {
                    count++;
                }
            }
        }
        catch
        {
            // partial count stands
        }
        return count;
    }

    private static int CountElements(Document doc, ElementFilter filter)
    {
        try
        {
            return new FilteredElementCollector(doc)
                .WherePasses(filter)
                .ToElements().Count;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountElements(Document doc, Type elementType, ISet<ElementId>? excludeIds = null)
    {
        try
        {
            var count = 0;
            foreach (var element in new FilteredElementCollector(doc).OfClass(elementType))
            {
                if (excludeIds is not null && excludeIds.Contains(element.Id))
                {
                    continue;
                }
                count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }
}
