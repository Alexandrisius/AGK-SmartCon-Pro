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
    private const int MaxBatchSheets = 50;

    public GetSheetContentTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_sheet_content",
        Description =
            "Shows what is placed on one sheet or on a batch: views with viewport coordinates and " +
            "box size in millimetres, and placed schedules (including revision schedules) with " +
            "their points and read-back size (widthMm/heightMm from the instance bounding box, " +
            "including Revit's fixed ~2.1 mm selection margin). Identify one sheet by sheetId, exact number or exact name; pass sheetIds " +
            "to read many sheets in one call (a sheet set audit does not need a round trip each). " +
            "An ambiguous name or a number that only matches after removing invisible characters " +
            "returns candidates (with numberCodePoints) instead of silently picking one. Paged over " +
            "views: limit (default 200, max 500), offset, returned, nextOffset, truncated; in batch " +
            "mode offset/limit/nextOffset apply to each sheet's view list. " +
            "Coordinates are paper-space millimetres from the sheet's bottom-left corner.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""sheetId"":{""type"":""integer""},""sheetIds"":{""type"":""array"",""description"":""Batch read: up to 50 sheet ids; returns sheets[]"",""items"":{""type"":""integer""}},""number"":{""type"":""string""},""name"":{""type"":""string""},""includeSchedules"":{""type"":""boolean"",""default"":true},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var sheetIds = AgentJson.GetLongArray(args, "sheetIds");
        if (sheetIds.Count > 0)
            return ExecuteBatch(doc, sheetIds, args, ct);

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
                "Pass sheetId, sheetIds, number or name to identify the sheet.");
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

        var content = BuildContent(doc, sheet!, includeSchedules, ct);
        var page = AgentPaging.Slice(content.Views, offset, limit);
        var hasMore = offset + page.Count < content.Views.Count;
        var scheduleCounts = AgentSheetReader.CountPlacedSchedules(doc);
        var allSheets = AgentSheetReader.Collect(doc, excludePlaceholders: false);
        var visualDuplicates = AgentSheetReader.FindVisualDuplicateNumbers(allSheets);
        var sheetCard = AgentSheetReader.CreateCard(sheet!, scheduleCounts, visualDuplicates);

        var payload = new
        {
            sheet = sheetCard,
            placedViewCount = content.Views.Count,
            offset,
            returned = page.Count,
            nextOffset = hasMore ? offset + page.Count : (int?)null,
            truncated = hasMore,
            views = page,
            placedScheduleCount = sheetCard.ScheduleCount,
            revisionScheduleCount = sheetCard.RevisionScheduleCount,
            schedules = content.Schedules
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Sheet " + sheetCard.Number + " '" + sheetCard.Name + "' holds " + content.Views.Count +
            " view(s) and " + sheetCard.ScheduleCount + " schedule(s)" +
            (includeSchedules
                ? string.Empty
                : " (schedule details not requested; pass includeSchedules=true)") +
            "; returned " + page.Count + " view(s)." +
            (hasMore ? " The view list is truncated — pass nextOffset as offset." : string.Empty));
    }

    /// <summary>
    /// Batch read of several sheets: "which of the eight sheets are empty and
    /// what is on the rest" used to cost one call per sheet.
    /// </summary>
    private ToolResult ExecuteBatch(
        Document doc,
        IReadOnlyList<long> sheetIds,
        JsonElement args,
        CancellationToken ct)
    {
        if (sheetIds.Count > MaxBatchSheets)
        {
            return ToolResult.Fail(
                "limit_exceeded",
                "At most " + MaxBatchSheets + " sheet ids per call are allowed; split the list.");
        }

        var includeSchedules = AgentJson.GetBool(args, "includeSchedules", true);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = Math.Max(1, Math.Min(AgentJson.GetInt(args, "limit", DefaultLimit), MaxLimit));
        var scheduleCounts = AgentSheetReader.CountPlacedSchedules(doc);
        var allSheets = AgentSheetReader.Collect(doc, excludePlaceholders: false);
        var visualDuplicates = AgentSheetReader.FindVisualDuplicateNumbers(allSheets);

        var rows = new List<object>(sheetIds.Count);
        var notFound = new List<long>();
        foreach (var sheetId in sheetIds)
        {
            ct.ThrowIfCancellationRequested();

            if (doc.GetElement(ElementIdCompat.Create(sheetId)) is not ViewSheet sheet)
            {
                notFound.Add(sheetId);
                continue;
            }

            var content = BuildContent(doc, sheet, includeSchedules, ct);
            var views = content.Views.Skip(offset).Take(limit).ToList();
            var hasMore = offset + views.Count < content.Views.Count;
            var card = AgentSheetReader.CreateCard(sheet, scheduleCounts, visualDuplicates);
            rows.Add(new
            {
                sheet = card,
                placedViewCount = content.Views.Count,
                offset,
                returned = views.Count,
                nextOffset = hasMore ? offset + views.Count : (int?)null,
                truncated = hasMore,
                views,
                placedScheduleCount = card.ScheduleCount,
                revisionScheduleCount = card.RevisionScheduleCount,
                schedules = content.Schedules
            });
        }

        if (rows.Count == 0 && notFound.Count > 0)
            return ToolResult.Fail("sheet_not_found", "None of the requested sheet ids exist.");

        var payload = new
        {
            mode = "batch",
            total = sheetIds.Count,
            returned = rows.Count,
            notFound = notFound.Count == 0 ? null : notFound,
            sheets = rows
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Read " + rows.Count + " sheet(s)." +
            (notFound.Count > 0
                ? " " + notFound.Count + " id(s) not found or not a sheet."
                : string.Empty));
    }

    private sealed class SheetContent
    {
        public List<object> Views { get; } = new();

        public List<object> Schedules { get; } = new();
    }

    private static SheetContent BuildContent(
        Document doc,
        ViewSheet sheet,
        bool includeSchedules,
        CancellationToken ct)
    {
        var content = new SheetContent();
        var viewRows = new List<(string SortKey, object Row)>();
        foreach (var viewportId in sheet.GetAllViewports())
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
        foreach (var row in viewRows)
            content.Views.Add(row.Row);

        if (!includeSchedules)
            return content;

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

            // N-ds-9: the placed table's real size, read back from the instance
            // bounding box (Revit adds a fixed ~2.1 mm selection margin around
            // the table — the same margin the placement tools report).
            double? widthMm = null;
            double? heightMm = null;
            try
            {
                var box = instance.get_BoundingBox(sheet);
                if (box is not null)
                {
                    widthMm = AgentUnitsFormat.ToMillimeters(box.Max.X - box.Min.X);
                    heightMm = AgentUnitsFormat.ToMillimeters(box.Max.Y - box.Min.Y);
                }
            }
            catch (Exception)
            {
                // Revision schedules and split segments may not expose a box.
            }

            content.Schedules.Add(new
            {
                instanceId = instance.Id.GetValue(),
                scheduleId = instance.ScheduleId.GetValue(),
                name = schedule?.Name,
                xMm = pointX,
                yMm = pointY,
                widthMm,
                heightMm,
                isRevisionSchedule = instance.IsTitleblockRevisionSchedule
            });
        }

        return content;
    }
}