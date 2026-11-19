using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetSheetContentTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public GetSheetContentTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_sheet_content",
        Description =
            "Shows what is placed on one sheet: views with viewport coordinates and box size in " +
            "millimetres, and placed schedules (including revision schedules) with their points. " +
            "Identify the sheet by sheetId, exact number or exact name; an ambiguous name returns " +
            "candidates. Paged over views: limit (default 200, max 500), offset, returned, " +
            "nextOffset, truncated. Coordinates are paper-space millimetres from the sheet's " +
            "bottom-left corner.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sheetId"":{""type"":""integer""},""number"":{""type"":""string""},""name"":{""type"":""string""},""includeSchedules"":{""type"":""boolean"",""default"":true},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var sheetId = AgentJson.GetLong(args, "sheetId", 0);
        var number = AgentJson.GetString(args, "number");
        var name = AgentJson.GetString(args, "name");
        var includeSchedules = AgentJson.GetBool(args, "includeSchedules", true);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = Math.Max(1, Math.Min(AgentJson.GetInt(args, "limit", DefaultLimit), MaxLimit));

        if (sheetId <= 0 && string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(name))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "Pass sheetId, number or name to identify the sheet.");
        }

        if (!AgentSheetReader.TryFind(
                doc, sheetId > 0 ? sheetId : null, number, name,
                out var sheet, out var candidates, out var error))
        {
            var failureData = candidates.Count > 0
                ? AgentJson.Serialize(new { candidates })
                : (JsonElement?)null;
            return ToolResult.Fail(
                candidates.Count > 0 ? "ambiguous_sheet" : "sheet_not_found",
                error ?? "The sheet was not found.",
                data: failureData);
        }

        var viewRows = new List<(string SortKey, object Row)>();
        foreach (var viewportId in sheet!.GetAllViewports())
        {
            ct.ThrowIfCancellationRequested();

            if (doc.GetElement(viewportId) is not Viewport viewport)
                continue;

            var view = doc.GetElement(viewport.ViewId) as View;
            double? centerX = null;
            double? centerY = null;
            double? boxWidth = null;
            double? boxHeight = null;
            try
            {
                var center = viewport.GetBoxCenter();
                if (center is not null)
                {
                    centerX = AgentUnitsFormat.ToMillimeters(center.X);
                    centerY = AgentUnitsFormat.ToMillimeters(center.Y);
                }

                var outline = viewport.GetBoxOutline();
                if (outline is not null)
                {
                    boxWidth = AgentUnitsFormat.ToMillimeters(
                        outline.MaximumPoint.X - outline.MinimumPoint.X);
                    boxHeight = AgentUnitsFormat.ToMillimeters(
                        outline.MaximumPoint.Y - outline.MinimumPoint.Y);
                }
            }
            catch (Exception)
            {
                // A viewport being regenerated can report no box; keep the row.
            }

            var viewName = view?.Name ?? string.Empty;
            viewRows.Add((viewName, new
            {
                viewportId = viewportId.GetValue(),
                viewId = viewport.ViewId.GetValue(),
                name = view?.Name,
                viewType = view?.ViewType.ToString(),
                centerXMm = centerX,
                centerYMm = centerY,
                boxWidthMm = boxWidth,
                boxHeightMm = boxHeight
            }));
        }

        viewRows.Sort((left, right) =>
            string.Compare(left.SortKey, right.SortKey, StringComparison.OrdinalIgnoreCase));
        var views = new List<object>(viewRows.Count);
        foreach (var row in viewRows)
            views.Add(row.Row);

        var schedules = new List<object>();
        if (includeSchedules)
        {
            foreach (var element in new FilteredElementCollector(doc)
                         .OfClass(typeof(ScheduleSheetInstance)))
            {
                ct.ThrowIfCancellationRequested();

                if (element is not ScheduleSheetInstance instance)
                    continue;
                if (instance.OwnerViewId is null || instance.OwnerViewId != sheet.Id)
                    continue;

                var schedule = doc.GetElement(instance.ScheduleId) as ViewSchedule;
                double? pointX = null;
                double? pointY = null;
                try
                {
                    var point = instance.Point;
                    if (point is not null)
                    {
                        pointX = AgentUnitsFormat.ToMillimeters(point.X);
                        pointY = AgentUnitsFormat.ToMillimeters(point.Y);
                    }
                }
                catch (Exception)
                {
                    // Revision schedules may not expose a point.
                }

                schedules.Add(new
                {
                    instanceId = instance.Id.GetValue(),
                    scheduleId = instance.ScheduleId.GetValue(),
                    name = schedule?.Name,
                    xMm = pointX,
                    yMm = pointY,
                    isRevisionSchedule = instance.IsTitleblockRevisionSchedule
                });
            }
        }

        var page = AgentPaging.Slice(views, offset, limit);
        var hasMore = offset + page.Count < views.Count;
        var scheduleCounts = AgentSheetReader.CountPlacedSchedules(doc);
        var sheetCard = AgentSheetReader.CreateCard(sheet, scheduleCounts);

        var payload = new
        {
            sheet = sheetCard,
            placedViewCount = views.Count,
            offset,
            returned = page.Count,
            nextOffset = hasMore ? offset + page.Count : (int?)null,
            truncated = hasMore || offset > 0,
            views = page,
            placedScheduleCount = sheetCard.ScheduleCount,
            schedules
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Sheet " + sheetCard.Number + " '" + sheetCard.Name + "' holds " + views.Count +
            " view(s) and " + sheetCard.ScheduleCount + " schedule(s)" +
            (includeSchedules
                ? string.Empty
                : " (schedule details not requested; pass includeSchedules=true)") +
            "; returned " + page.Count + " view(s)." +
            (hasMore ? " The view list is truncated — pass nextOffset as offset." : string.Empty));
    }
}