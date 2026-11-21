using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Places views on a sheet in one batch: one call = one transaction = one
/// change set. Idempotent per item: a view already placed on this sheet is
/// skipped with a no-op report instead of creating a second viewport.
/// </summary>
public sealed class CreateViewportsTool : AgentWriteToolBase
{
    private const int MaxItems = 100;

    public CreateViewportsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_viewports",
        Description =
            "Places views on a sheet (up to 100 in one call; one transaction, one change set). " +
            "Each item: viewId plus pointMmX/pointMmY — the TARGET CENTER of the viewport in " +
            "sheet millimetres from the bottom-left corner (take the coordinates from " +
            "get_sheet_content). After creation the actual center is read back and returned as " +
            "actualMmX/actualMmY. Templates, schedules and sheets are not placeable " +
            "(view_not_placeable); a view already on this sheet is skipped (no-op); a view " +
            "already on another sheet fails with view_already_placed — duplicate it first. " +
            "Idempotent for already placed views; dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sheetId"":{""type"":""integer""},""items"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""pointMmX"":{""type"":""number"",""description"":""Viewport center X in mm from the sheet bottom-left corner""},""pointMmY"":{""type"":""number"",""description"":""Viewport center Y in mm from the sheet bottom-left corner""}},""required"":[""viewId"",""pointMmX"",""pointMmY""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""sheetId"",""items""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var sheetId = AgentJson.GetLong(args, "sheetId", 0);
        if (doc.GetElement(ElementIdCompat.Create(sheetId)) is not ViewSheet sheet)
        {
            outcome.Success = false;
            outcome.ErrorCode = "sheet_not_found";
            outcome.Message = "Element " + sheetId + " is not a sheet.";
            return;
        }

        var request = AgentJson.Deserialize<CreateViewportsRequest>(args);
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "no_items";
            outcome.Message = "The items array is empty; pass at least one view placement.";
            return;
        }

        if (items.Count > MaxItems)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxItems + " view placements per call are allowed.";
            return;
        }

        outcome.Description = "Place " + items.Count + " view(s) on sheet " + sheet.SheetNumber;

        var placedViewIds = CollectPlacedViewIds(doc, sheet);
        var ownerSheetByView = CollectViewportOwners(doc, sheet.Id);
        // Viewport.Create returns null (instead of throwing) when the document
        // still needs a regeneration after earlier modifications.
        doc.Regenerate();
        var rows = new List<object>(items.Count);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = item.ViewId };
            if (doc.GetElement(ElementIdCompat.Create(item.ViewId)) is not View view)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "view_not_found",
                    "Element " + item.ViewId + " does not exist in the active document.");
                rows.Add(ItemRow(item, null, null, "failed"));
                continue;
            }

            report.Name = view.Name;

            if (placedViewIds.Contains(item.ViewId))
            {
                report.Reason = "The view is already placed on this sheet (no-op).";
                outcome.Batch.Skipped.Add(report);
                rows.Add(ItemRow(item, null, null, "skipped"));
                continue;
            }

            if (!IsPlaceable(view))
            {
                ApplyViewTemplateTool.Fail(outcome, report, "view_not_placeable",
                    "View '" + view.Name + "' (" + view.ViewType + ") cannot be placed with " +
                    "Viewport.Create. Templates, schedules and sheets are not placeable; " +
                    "schedules need their own placement mechanism.");
                rows.Add(ItemRow(item, null, null, "failed"));
                continue;
            }

            if (view.ViewType != ViewType.Legend
                && ownerSheetByView.TryGetValue(item.ViewId, out var ownerNumber))
            {
                ApplyViewTemplateTool.Fail(outcome, report, "view_already_placed",
                    "View '" + view.Name + "' is already placed on sheet '" + ownerNumber +
                    "'. Duplicate the view (or pick another one) to place it here.");
                rows.Add(ItemRow(item, null, null, "failed"));
                continue;
            }

            if (!item.PointMmX.HasValue || !item.PointMmY.HasValue)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "invalid_arguments",
                    "pointMmX and pointMmY are required for every item.");
                rows.Add(ItemRow(item, null, null, "failed"));
                continue;
            }

            Viewport viewport;
            try
            {
                var pointX = AgentSheetWriteHelper.MillimetersToInternal(item.PointMmX.Value);
                var pointY = AgentSheetWriteHelper.MillimetersToInternal(item.PointMmY.Value);
                var point = new XYZ(pointX, pointY, 0);
                viewport = Viewport.Create(doc, sheet.Id, view.Id, point);
                if (viewport is null)
                {
                    // Viewport.Create returns null (instead of throwing) when the
                    // document was not regenerated after its last geometry change;
                    // one regeneration makes the same call succeed (IT-verified
                    // on Revit 2025, see AgentSheetWriteTests).
                    doc.Regenerate();
                    viewport = Viewport.Create(doc, sheet.Id, view.Id, point);
                }

                doc.Regenerate();
            }
            catch (Exception ex)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "view_not_placeable",
                    "Revit refused to place '" + view.Name + "' on sheet '" + sheet.SheetNumber +
                    "': " + ex.Message);
                rows.Add(ItemRow(item, null, null, "failed"));
                continue;
            }

            if (viewport is null)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "view_not_placeable",
                    "Revit returned no viewport for '" + view.Name + "' on sheet '" +
                    sheet.SheetNumber +
                    "'. Check that the view is not already placed on another sheet.");
                rows.Add(ItemRow(item, null, null, "failed"));
                continue;
            }

            placedViewIds.Add(item.ViewId);
            double actualMmX;
            double actualMmY;
            try
            {
                var center = viewport.GetBoxCenter();
                actualMmX = AgentUnitsFormat.ToMillimeters(center.X);
                actualMmY = AgentUnitsFormat.ToMillimeters(center.Y);
            }
            catch (Exception)
            {
                actualMmX = item.PointMmX.Value;
                actualMmY = item.PointMmY.Value;
            }

            report.NewValue = AgentSheetWriteHelper.CenterText(actualMmX, actualMmY);
            report.Detail = "Requested center " +
                            AgentSheetWriteHelper.CenterText(item.PointMmX.Value, item.PointMmY.Value) +
                            " mm";
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = viewport.UniqueId,
                ElementId = viewport.Id.GetValue(),
                ParameterName = "viewport_created",
                NewValue = view.Name,
                Status = ChangeStatus.Applied
            });
            rows.Add(ItemRow(item, actualMmX, actualMmY, "placed"));
        }

        outcome.Data["sheetId"] = sheet.Id.GetValue();
        outcome.Data["items"] = rows;
    }

    private static HashSet<long> CollectPlacedViewIds(Document doc, ViewSheet sheet)
    {
        var viewIds = new HashSet<long>();
        foreach (var viewportId in sheet.GetAllViewports())
        {
            if (doc.GetElement(viewportId) is Viewport viewport)
                viewIds.Add(viewport.ViewId.GetValue());
        }

        return viewIds;
    }

    private static bool IsPlaceable(View view)
    {
        if (view.IsTemplate)
            return false;

        if (view is ViewSchedule or ViewSheet)
            return false;

        return true;
    }

    /// <summary>
    /// Sheets that already hold a viewport of a view, by view id. Viewports not
    /// assigned to a sheet (SheetId invalid) and viewports of the target sheet
    /// are not owners: the first is not a placement, the second is a no-op skip.
    /// </summary>
    private static Dictionary<long, string> CollectViewportOwners(Document doc, ElementId sheetId)
    {
        var owners = new Dictionary<long, string>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(Viewport)))
        {
            if (element is not Viewport viewport)
                continue;

            var ownerSheetId = viewport.SheetId;
            if (ownerSheetId == ElementId.InvalidElementId || ownerSheetId == sheetId)
                continue;

            owners[viewport.ViewId.GetValue()] =
                (doc.GetElement(ownerSheetId) as ViewSheet)?.SheetNumber ?? string.Empty;
        }

        return owners;
    }

    private static object ItemRow(
        ViewportPlacementItem item,
        double? actualMmX,
        double? actualMmY,
        string status) => new
    {
        viewId = item.ViewId,
        requestedMmX = item.PointMmX,
        requestedMmY = item.PointMmY,
        actualMmX,
        actualMmY,
        status
    };

    private sealed class CreateViewportsRequest
    {
        public List<ViewportPlacementItem>? Items { get; set; }
    }

    private sealed class ViewportPlacementItem
    {
        public long ViewId { get; set; }

        public double? PointMmX { get; set; }

        public double? PointMmY { get; set; }
    }
}
