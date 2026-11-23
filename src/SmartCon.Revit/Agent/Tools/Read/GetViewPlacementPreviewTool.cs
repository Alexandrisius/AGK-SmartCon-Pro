using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Paper-size estimate for views before they go on sheets: a placed view
/// reports its real viewport box; a cropped plan/section converts its crop box
/// through the view scale; an uncropped or perspective 3D view is reported as
/// not estimable — Revit exposes a 3D view's paper extents only after a
/// viewport exists.
/// </summary>
public sealed class GetViewPlacementPreviewTool : AgentReadToolBase
{
    private const int MaxViews = 20;

    public GetViewPlacementPreviewTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_view_placement_preview",
        Description =
            "Estimates how much paper each view needs (up to 20 viewIds per call) before " +
            "assembling sheets. A view already placed on any sheet returns its ACTUAL box " +
            "width/height in millimetres from the viewport (estimated=false, placedOnSheet). " +
            "A plan/section/legend with an active crop returns the crop box size divided by " +
            "the view scale (estimated=true). A 3D view WITHOUT a crop (or perspective) is " +
            "reported as estimated=false with no size: the Revit API does not expose a 3D " +
            "view's paper extents before placement — the size is learned by placing a " +
            "viewport (build_system_sheets measures after placement and reports the real box). " +
            "Templates, schedules and sheets are reported as not placeable. Optional sheetId " +
            "also returns that sheet's size for comparison.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""viewIds"":{""type"":""array"",""minItems"":1,""maxItems"":20,""items"":{""type"":""integer""},""description"":""View ids to estimate""},""sheetId"":{""type"":""integer"",""description"":""Optional sheet: its width/height are returned for comparison""}},""required"":[""viewIds""],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var viewIds = AgentJson.GetLongArray(args, "viewIds");
        if (viewIds.Count == 0)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "Pass viewIds (1..20 view ids to estimate).");
        }

        if (viewIds.Count > MaxViews)
        {
            return ToolResult.Fail(
                "limit_exceeded",
                "At most " + MaxViews + " views per call are allowed.");
        }

        object? sheetInfo = null;
        var sheetId = AgentJson.GetLong(args, "sheetId", 0);
        if (sheetId > 0)
        {
            if (doc.GetElement(ElementIdCompat.Create(sheetId)) is not ViewSheet sheet)
            {
                return ToolResult.Fail(
                    "sheet_not_found",
                    "Element " + sheetId + " is not a sheet.");
            }

            sheetInfo = new
            {
                sheetId = sheet.Id.GetValue(),
                number = sheet.SheetNumber,
                name = sheet.Name,
                widthMm = SheetWidthMm(sheet),
                heightMm = SheetHeightMm(sheet)
            };
        }

        var viewportByView = CollectViewportsByView(doc);
        var rows = new List<object>(viewIds.Count);
        foreach (var viewId in viewIds)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(DescribeView(doc, viewId, viewportByView));
        }

        var payload = new { sheet = sheetInfo, views = rows };
        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Estimated " + rows.Count + " view(s)." +
            (sheetInfo is null ? string.Empty : " Sheet size included for comparison."));
    }

    private static Dictionary<long, Viewport> CollectViewportsByView(Document doc)
    {
        var viewports = new Dictionary<long, Viewport>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(Viewport)))
        {
            if (element is Viewport viewport)
                viewports[viewport.ViewId.GetValue()] = viewport;
        }

        return viewports;
    }

    private static object DescribeView(
        Document doc,
        long viewId,
        Dictionary<long, Viewport> viewportByView)
    {
        if (doc.GetElement(ElementIdCompat.Create(viewId)) is not View view)
        {
            return new { viewId, status = "not_found" };
        }

        var kind = DescribeKind(view);
        if (view.IsTemplate || view is ViewSchedule or ViewSheet)
        {
            return new
            {
                viewId,
                name = view.Name,
                kind,
                status = "not_placeable",
                note = "Templates, schedules and sheets cannot be placed as viewports."
            };
        }

        if (viewportByView.TryGetValue(viewId, out var viewport))
        {
            double? boxWidthMm = null;
            double? boxHeightMm = null;
            string? placedOnSheet = null;
            try
            {
                var outline = viewport.GetBoxOutline();
                boxWidthMm = AgentUnitsFormat.ToMillimeters(
                    outline.MaximumPoint.X - outline.MinimumPoint.X);
                boxHeightMm = AgentUnitsFormat.ToMillimeters(
                    outline.MaximumPoint.Y - outline.MinimumPoint.Y);
                placedOnSheet = (doc.GetElement(viewport.SheetId) as ViewSheet)?.SheetNumber;
            }
            catch (Exception)
            {
                // A viewport mid-regeneration may report no box; fall through to
                // the estimate below.
            }

            if (boxWidthMm.HasValue && boxHeightMm.HasValue)
            {
                return new
                {
                    viewId,
                    name = view.Name,
                    kind,
                    status = "ok",
                    estimated = false,
                    source = "placed",
                    placedOnSheet,
                    boxWidthMm,
                    boxHeightMm
                };
            }
        }

        return DescribeUnplaced(view, viewId, kind);
    }

    private static object DescribeUnplaced(View view, long viewId, string kind)
    {
        const string no3dNote =
            "The Revit API does not expose a 3D view's paper size before placement " +
            "(uncropped or perspective); the size is learned only by placing a viewport — " +
            "build_system_sheets places first, measures after and reports the real box.";

        if (view is View3D)
            return new { viewId, name = view.Name, kind, status = "ok", estimated = false, note = no3dNote };

        var scale = view.Scale;
        var cropActive = false;
        try
        {
            cropActive = view.CropBoxActive;
        }
        catch (Exception)
        {
        }

        if (!cropActive || scale <= 0)
        {
            return new
            {
                viewId,
                name = view.Name,
                kind,
                status = "ok",
                estimated = false,
                note = "The view has no active crop box (or no scale), so its paper size is " +
                       "unknown before placement."
            };
        }

        try
        {
            var crop = view.CropBox;
            var paperWidthMm = AgentUnitsFormat.ToMillimeters(
                (crop.Max.X - crop.Min.X) / scale);
            var paperHeightMm = AgentUnitsFormat.ToMillimeters(
                (crop.Max.Y - crop.Min.Y) / scale);
            return new
            {
                viewId,
                name = view.Name,
                kind,
                status = "ok",
                estimated = true,
                source = "crop",
                paperWidthMm,
                paperHeightMm,
                scale
            };
        }
        catch (Exception ex)
        {
            return new
            {
                viewId,
                name = view.Name,
                kind,
                status = "ok",
                estimated = false,
                note = "The crop box could not be read: " + ex.Message
            };
        }
    }

    private static string DescribeKind(View view) => view switch
    {
        View3D => "3d",
        ViewPlan => "plan",
        ViewSection => "section",
        _ when view.ViewType == ViewType.Legend => "legend",
        _ => view.ViewType.ToString().ToLowerInvariant()
    };

    internal static double? SheetWidthMm(ViewSheet sheet)
    {
        try
        {
            var outline = sheet.Outline;
            var width = outline.Max.U - outline.Min.U;
            return width > 0 ? AgentUnitsFormat.ToMillimeters(width) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static double? SheetHeightMm(ViewSheet sheet)
    {
        try
        {
            var outline = sheet.Outline;
            var height = outline.Max.V - outline.Min.V;
            return height > 0 ? AgentUnitsFormat.ToMillimeters(height) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
