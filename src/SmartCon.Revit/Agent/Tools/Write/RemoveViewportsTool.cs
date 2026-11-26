using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Removes viewport placements in one batch: only the Viewport elements are
/// deleted — every view stays in the project (returned as viewsKept). undo_last
/// restores the placements by re-creating the viewports at the recorded
/// centers (change item kind "viewport_removed").
/// </summary>
public sealed class RemoveViewportsTool : AgentWriteToolBase
{
    private const int MaxItems = 100;

    public RemoveViewportsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "remove_viewports",
        Description =
            "Removes viewport placements: the view is taken OFF the sheet, the view itself " +
            "stays in the project (the answer lists the kept views). Pass viewportIds[] (from " +
            "get_sheet_content) or sheetId plus viewIds[] to unplace those views from that " +
            "sheet; up to 100 placements per call, one transaction, one change set. " +
            "undo_last restores the placements at the recorded centers. Dry run defaults " +
            "to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewportIds"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""integer""},""description"":""Viewport element ids from get_sheet_content""},""sheetId"":{""type"":""integer"",""description"":""Together with viewIds[]: remove the placements of these views from this sheet""},""viewIds"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""integer""},""description"":""View ids to unplace from sheetId""},""dryRun"":{""type"":""boolean"",""default"":true}},""anyOf"":[{""required"":[""viewportIds""]},{""required"":[""sheetId"",""viewIds""]}],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var hasViewportIds = args.TryGetProperty("viewportIds", out var viewportIdsElement);
        var hasViewIds = args.TryGetProperty("viewIds", out var viewIdsElement);
        if (!hasViewportIds && !hasViewIds)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message =
                "Pass viewportIds[] or sheetId plus viewIds[] to identify the placements.";
            return;
        }

        if (hasViewportIds && hasViewIds)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass either viewportIds[] or sheetId plus viewIds[], not both.";
            return;
        }

        var targets = new List<(long ViewportId, long ViewId)>();
        var resolveRows = new List<object>();
        if (hasViewportIds)
        {
            var ids = AgentJson.GetLongArray(args, "viewportIds");
            if (ids.Count == 0)
            {
                outcome.Success = false;
                outcome.ErrorCode = "no_items";
                outcome.Message = "viewportIds is empty; pass at least one viewport id.";
                return;
            }

            if (ids.Count > MaxItems)
            {
                outcome.Success = false;
                outcome.ErrorCode = "limit_exceeded";
                outcome.Message = "At most " + MaxItems + " placements per call are allowed.";
                return;
            }

            foreach (var id in ids)
                targets.Add((id, 0));
        }
        else
        {
            var sheetId = AgentJson.GetLong(args, "sheetId", 0);
            if (doc.GetElement(ElementIdCompat.Create(sheetId)) is not ViewSheet sheet)
            {
                outcome.Success = false;
                outcome.ErrorCode = "sheet_not_found";
                outcome.Message = "Element " + sheetId + " is not a sheet.";
                return;
            }

            var viewIds = AgentJson.GetLongArray(args, "viewIds");
            if (viewIds.Count == 0)
            {
                outcome.Success = false;
                outcome.ErrorCode = "no_items";
                outcome.Message = "viewIds is empty; pass at least one view id.";
                return;
            }

            if (viewIds.Count > MaxItems)
            {
                outcome.Success = false;
                outcome.ErrorCode = "limit_exceeded";
                outcome.Message = "At most " + MaxItems + " placements per call are allowed.";
                return;
            }

            var viewportsByView = CollectSheetViewports(doc, sheet.Id);
            foreach (var viewId in viewIds)
            {
                if (!viewportsByView.TryGetValue(viewId, out var viewportId))
                {
                    var report = new WriteItemReport { Id = viewId };
                    var viewName = (doc.GetElement(ElementIdCompat.Create(viewId)) as View)?.Name;
                    report.Name = viewName;
                    Fail(outcome, report, "viewport_not_found",
                        "View " + (viewName ?? viewId.ToString()) + " is not placed on sheet '" +
                        sheet.SheetNumber + "' (no viewport to remove).");
                    resolveRows.Add(new
                    {
                        viewId,
                        viewName,
                        status = "failed"
                    });
                    continue;
                }

                targets.Add((viewportId, viewId));
            }
        }

        outcome.Description = "Remove " + targets.Count + " viewport placement(s)";

        var rows = new List<object>(targets.Count);
        var viewsKept = new List<string>();
        var removedViews = new List<long>();

        foreach (var (viewportId, knownViewId) in targets)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = viewportId };
            if (doc.GetElement(ElementIdCompat.Create(viewportId)) is not Viewport viewport)
            {
                Fail(outcome, report, "viewport_not_found",
                    "Element " + viewportId + " is not a viewport.");
                rows.Add(new { viewportId, status = "failed" });
                continue;
            }

            var view = doc.GetElement(viewport.ViewId) as View;
            var sheet = doc.GetElement(viewport.SheetId) as ViewSheet;
            var viewName = view?.Name ?? ("view " + knownViewId);
            report.Name = (sheet is null ? "?" : sheet.SheetNumber) + " - " + viewName;

            double centerMmX;
            double centerMmY;
            try
            {
                var center = viewport.GetBoxCenter();
                centerMmX = AgentUnitsFormat.ToMillimeters(center.X);
                centerMmY = AgentUnitsFormat.ToMillimeters(center.Y);
            }
            catch (Exception)
            {
                centerMmX = 0;
                centerMmY = 0;
            }

            // Snapshot everything before Delete: a deleted viewport throws
            // InvalidObjectException on any property access.
            var viewIdValue = viewport.ViewId.GetValue();
            var elementIdValue = viewport.Id.GetValue();
            var uniqueId = viewport.UniqueId;
            var sheetNumber = sheet?.SheetNumber;

            report.OldValue = AgentSheetWriteHelper.PlacementText(
                viewIdValue, viewport.SheetId.GetValue(), centerMmX, centerMmY);
            report.NewValue = viewName;

            if (WorksharingScope.TryGetBlock(viewport.Id, out var block))
            {
                Skip(outcome, report, block);
                rows.Add(new { viewportId, viewId = knownViewId, status = "skipped" });
                continue;
            }

            try
            {
                doc.Delete(viewport.Id);
            }
            catch (Exception ex)
            {
                Fail(outcome, report, "remove_failed",
                    "Revit refused to remove the viewport of '" + viewName + "': " + ex.Message);
                rows.Add(new { viewportId, viewId = knownViewId, status = "failed" });
                continue;
            }

            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;
            viewsKept.Add(viewName);
            removedViews.Add(viewIdValue);
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = uniqueId,
                ElementId = elementIdValue,
                ParameterName = "viewport_removed",
                OldValue = report.OldValue,
                NewValue = viewName,
                Status = ChangeStatus.Applied
            });
            rows.Add(new
            {
                viewportId = elementIdValue,
                viewId = viewIdValue,
                viewName,
                sheetNumber,
                status = "removed"
            });
        }

        doc.Regenerate();

        foreach (var viewId in removedViews)
        {
            if (doc.GetElement(ElementIdCompat.Create(viewId)) is null)
                outcome.Warnings.Add(
                    "View " + viewId +
                    " disappeared together with its viewport; it was not kept in the project.");
        }

        foreach (var row in resolveRows)
            rows.Add(row);

        outcome.Data["items"] = rows;
        outcome.Data["viewsKept"] = viewsKept;
    }

    private static Dictionary<long, long> CollectSheetViewports(Document doc, ElementId sheetId)
    {
        var map = new Dictionary<long, long>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(Viewport)))
        {
            if (element is not Viewport viewport || viewport.SheetId != sheetId)
                continue;

            map[viewport.ViewId.GetValue()] = viewport.Id.GetValue();
        }

        return map;
    }

    private static void Fail(
        WriteOutcome outcome,
        WriteItemReport report,
        string errorCode,
        string reason)
    {
        report.ErrorCode = errorCode;
        report.Reason = reason;
        outcome.Batch.Failed.Add(report);
        outcome.ErrorCode ??= errorCode;
    }
}
