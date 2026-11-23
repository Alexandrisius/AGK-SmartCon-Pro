using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Read tool: placement and extents of model elements. Reports the element's
/// location (insertion point, or the start/end/midpoint of its location curve
/// for pipes, ducts, walls, beams) and its axis-aligned bounding box in Revit
/// model coordinates, in millimetres like the other geometric tools of the
/// agent (connector origins, level elevations). A type has no placement and is
/// reported as not applicable. Read-only: opens no transaction.
/// </summary>
public sealed class GetElementBoundsTool : AgentReadToolBase
{
    private const int MaxElements = 50;

    public GetElementBoundsTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_element_bounds",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Returns the placement and extents of up to 50 model elements: location (kind " +
            "point with an insertion point for family instances and similar elements; kind " +
            "curve with start, end, mid and lengthMm for pipes, ducts, walls, beams and other " +
            "curve-driven elements) and the axis-aligned bounding box (min, max, size, center) " +
            "in Revit model coordinates. All coordinates are in millimetres (units=mm), like " +
            "the connector origins of get_element_relations and the level elevations of " +
            "list_levels; projectLengthUnits names the length unit of the project for " +
            "reference. The box is aligned to the model axes and encloses all geometry of the " +
            "element, including geometry that is not obvious (family flip controls). Pass " +
            "viewId to request view-specific (e.g. cut) boxes - when a view box cannot be " +
            "calculated Revit returns the model box. An element type (FamilySymbol, WallType) " +
            "has no placement: it is returned with applicable=false and reason element_type - " +
            "ask for its instances (query_elements typeIds). Elements without a location and " +
            "without model geometry (views, sheets, schedules) are applicable=false with " +
            "reason no_placement. Use it to see where elements sit, how far apart they are and " +
            "where a system gap is. Read-only.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""elementIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":50,""description"":""Element ids (instances), up to 50""},""viewId"":{""type"":""integer"",""description"":""Optional view: the bounding boxes are requested in the context of this view (cut geometry); omit for the model box""}},""required"":[""elementIds""],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope(
            "AgentTool",
            ("Method", nameof(GetElementBoundsTool.Execute)),
            ("Document", SafeTitle(doc)));

        var elementIds = AgentJson.GetLongArray(args, "elementIds");
        if (elementIds.Count == 0)
            return ToolResult.Fail("invalid_arguments", "elementIds must contain at least one id.");

        if (elementIds.Count > MaxElements)
        {
            return ToolResult.Fail(
                "too_many_elements",
                "At most " + MaxElements + " element ids are allowed per call.");
        }

        View? view = null;
        long? requestedViewId = null;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("viewId", out var rawViewId)
            && rawViewId.ValueKind != JsonValueKind.Null)
        {
            if (rawViewId.ValueKind != JsonValueKind.Number || !rawViewId.TryGetInt64(out var viewNumber))
            {
                return ToolResult.Fail(
                    "invalid_arguments",
                    "viewId must be the integer element id of a view.");
            }

            if (doc.GetElement(ElementIdCompat.Create(viewNumber)) is not View requested)
            {
                return ToolResult.Fail(
                    "view_not_found",
                    "Element " + viewNumber + " (viewId) is not a view in this document.");
            }

            view = requested;
            requestedViewId = viewNumber;
        }

        var rows = new List<object>();
        var missing = new List<long>();
        var withLocation = 0;
        var withBox = 0;
        var types = 0;
        var notApplicable = 0;

        foreach (var id in elementIds)
        {
            ct.ThrowIfCancellationRequested();

            var element = doc.GetElement(ElementIdCompat.Create(id));
            if (element is null)
            {
                missing.Add(id);
                continue;
            }

            var category = CategoryName(element);
            if (element is ElementType)
            {
                types++;
                notApplicable++;
                rows.Add(Row(
                    id, category, true, false, "element_type",
                    "A type has no placement in the model; query_elements(typeIds=[" + id +
                    "]) returns its instances.",
                    null, null, null));
                continue;
            }

            var location = ReadLocation(element);
            if (location is not null)
                withLocation++;

            var box = ReadBox(element, view, out var boxProblem);
            if (box is not null)
                withBox++;

            if (location is null && box is null)
            {
                notApplicable++;
                rows.Add(Row(
                    id, category, false, false, "no_placement",
                    "The element has neither a location nor model geometry (a view, sheet, " +
                    "schedule, annotation or datum element).",
                    null, null, boxProblem));
                continue;
            }

            rows.Add(Row(id, category, false, true, null, null, location, box, boxProblem));
        }

        if (rows.Count == 0)
        {
            return ToolResult.Fail(
                "element_not_found",
                "None of the requested element ids exist in this document.");
        }

        var payload = new
        {
            total = rows.Count,
            missing,
            units = "mm",
            coordinates = "model",
            projectLengthUnits = SafeProjectLengthUnits(doc),
            context = view is null ? "model" : "view",
            viewId = requestedViewId,
            withLocation,
            withBox,
            types,
            notApplicable,
            elements = rows
        };

        var message = "Bounds of " + rows.Count + " element(s) in mm, Revit model coordinates: " +
                      withLocation + " with a location, " + withBox + " with a bounding box" +
                      (types > 0 ? ", " + types + " type(s) not applicable" : string.Empty) +
                      (notApplicable > types ? ", " + (notApplicable - types) + " without placement" : string.Empty) +
                      (view is null ? string.Empty : " (bounding boxes requested in the context of the view)") +
                      ".";
        if (types > 0)
        {
            message += " A type has no placement: ask for its instances with " +
                       "query_elements(typeIds=[...]).";
        }

        return ToolResult.Ok(AgentJson.Serialize(payload), message);

        object Row(
            long id,
            string? category,
            bool isType,
            bool applicable,
            string? reason,
            string? note,
            object? location,
            object? box,
            string? problem) =>
            new
            {
                elementId = id,
                category,
                isElementType = isType,
                applicable,
                reason,
                note,
                location,
                boundingBox = box,
                boundingBoxProblem = problem
            };
    }

    /// <summary>
    /// Where the element sits: an insertion point, or the start/end/mid of
    /// its location curve. Null when the element has no location.
    /// </summary>
    private static object? ReadLocation(Element element)
    {
        try
        {
            switch (element.Location)
            {
                case LocationPoint point when point.Point is not null:
                    return new { kind = "point", point = Mm(point.Point) };
                case LocationCurve locationCurve when locationCurve.Curve is Curve { IsBound: true } curve:
                    return new
                    {
                        kind = "curve",
                        start = Mm(curve.GetEndPoint(0)),
                        end = Mm(curve.GetEndPoint(1)),
                        mid = SafeMid(curve),
                        lengthMm = Mm(curve.Length)
                    };
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "get_element_bounds could not read the location of element " +
                ElementIdOf(element) + ": " + ex.GetType().Name + ": " + ex.Message +
                " [Action: the element is reported by its bounding box only; if the location " +
                "is needed, read the element with get_element_details]");
            return null;
        }
    }

    private static object? SafeMid(Curve curve)
    {
        try
        {
            return Mm(curve.Evaluate(0.5, true));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? ReadBox(Element element, View? view, out string? problem)
    {
        problem = null;
        try
        {
            var box = element.get_BoundingBox(view);
            if (box is null)
                return null;

            if (!box.Enabled)
            {
                problem = "The bounding box of this element is empty (BoundingBoxXYZ.Enabled is false).";
                return null;
            }

            var min = box.Min;
            var max = box.Max;
            if (min is null || max is null)
                return null;

            var size = max - min;
            var center = (min + max) / 2.0;
            return new
            {
                min = Mm(min),
                max = Mm(max),
                size = Mm(size),
                center = Mm(center)
            };
        }
        catch (Exception ex)
        {
            problem = "The bounding box could not be read: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    private static string? CategoryName(Element element)
    {
        try
        {
            return element.Category?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SafeTitle(Document document)
    {
        try
        {
            return document.Title ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeProjectLengthUnits(Document document)
    {
        try
        {
            return AgentUnitsFormat.GetLengthUnitsLabel(document);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static long ElementIdOf(Element element)
    {
        try
        {
            return element.Id.GetValue();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Millimetres rounded to 0.001 mm: no floating-point noise, and precise
    /// enough for millimetre gaps between neighbouring elements.
    /// </summary>
    private static double Mm(double internalFeet) =>
        Math.Round(internalFeet * SmartCon.Core.Units.FeetToMm, 3, MidpointRounding.AwayFromZero);

    private static object Mm(XYZ point) =>
        new { x = Mm(point.X), y = Mm(point.Y), z = Mm(point.Z) };
}
