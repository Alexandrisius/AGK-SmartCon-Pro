using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Moves viewports in one batch: one call = one transaction = one change set.
/// Each item targets the new CENTER of the viewport box in sheet millimetres;
/// a shared shiftMm moves every listed viewport by dx/dy relative to its
/// current center instead (per-item pointMmX/pointMmY are ignored then).
/// </summary>
public sealed class MoveViewportsTool : AgentWriteToolBase
{
    private const int MaxItems = 100;

    public MoveViewportsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "move_viewports",
        Description =
            "Moves up to 100 viewports in one call (one transaction, one change set). " +
            "Each item: viewportId plus pointMmX/pointMmY — the new CENTER of the viewport " +
            "box in sheet millimetres from the bottom-left corner. With shiftMm {dx,dy} " +
            "every listed viewport moves by dx/dy from its current center and the per-item " +
            "coordinates are ignored. The actual center of each viewport is read back after " +
            "the move (actualMmX/actualMmY per item). Take viewport ids and current " +
            "coordinates from get_sheet_content. Idempotent per item; dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""object"",""properties"":{""viewportId"":{""type"":""integer""},""pointMmX"":{""type"":""number"",""description"":""Target viewport center X in mm from the sheet bottom-left corner (ignored when shiftMm is set)""},""pointMmY"":{""type"":""number"",""description"":""Target viewport center Y in mm from the sheet bottom-left corner (ignored when shiftMm is set)""}},""required"":[""viewportId""],""additionalProperties"":false}},""shiftMm"":{""type"":""object"",""description"":""Shift mode: every listed viewport moves by dx/dy from its current center"",""properties"":{""dx"":{""type"":""number"",""description"":""Shift along X in mm (positive = right)""},""dy"":{""type"":""number"",""description"":""Shift along Y in mm (positive = up)""}},""required"":[""dx"",""dy""],""additionalProperties"":false},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""items""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<MoveViewportsRequest>(args);
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "no_items";
            outcome.Message = "The items array is empty; pass at least one viewport move.";
            return;
        }

        if (items.Count > MaxItems)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxItems + " viewport moves per call are allowed.";
            return;
        }

        var shift = request!.ShiftMm;
        outcome.Description = "Move " + items.Count + " viewport(s)" +
                              (shift is null
                                  ? string.Empty
                                  : " by " + shift.Dx.ToString("0.###", CultureInfo.InvariantCulture) +
                                    ";" + shift.Dy.ToString("0.###", CultureInfo.InvariantCulture) + " mm");

        var moved = new List<PendingMove>();
        var rows = new List<object>(items.Count);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = item.ViewportId };
            if (doc.GetElement(ElementIdCompat.Create(item.ViewportId)) is not Viewport viewport)
            {
                Fail(outcome, report, "viewport_not_found",
                    "Element " + item.ViewportId + " is not a viewport.");
                rows.Add(new { viewportId = item.ViewportId, status = "failed" });
                continue;
            }

            var sheet = doc.GetElement(viewport.SheetId) as ViewSheet;
            report.Name = (sheet is null ? "?" : sheet.SheetNumber) +
                          " viewport " + item.ViewportId;

            double currentMmX;
            double currentMmY;
            try
            {
                var current = viewport.GetBoxCenter();
                currentMmX = AgentUnitsFormat.ToMillimeters(current.X);
                currentMmY = AgentUnitsFormat.ToMillimeters(current.Y);
            }
            catch (Exception ex)
            {
                Fail(outcome, report, "viewport_not_placeable",
                    "The viewport has no box center on a sheet: " + ex.Message);
                rows.Add(new { viewportId = item.ViewportId, status = "failed" });
                continue;
            }

            double targetMmX;
            double targetMmY;
            if (shift is not null)
            {
                targetMmX = currentMmX + shift.Dx;
                targetMmY = currentMmY + shift.Dy;
            }
            else if (!item.PointMmX.HasValue || !item.PointMmY.HasValue)
            {
                Fail(outcome, report, "invalid_arguments",
                    "pointMmX and pointMmY are required for every item unless shiftMm is set.");
                rows.Add(new { viewportId = item.ViewportId, status = "failed" });
                continue;
            }
            else
            {
                targetMmX = item.PointMmX.Value;
                targetMmY = item.PointMmY.Value;
            }

            report.OldValue = AgentSheetWriteHelper.CenterText(currentMmX, currentMmY);

            if (Math.Abs(currentMmX - targetMmX) < 0.05 && Math.Abs(currentMmY - targetMmY) < 0.05)
            {
                report.Reason = "The viewport is already at this center (no-op).";
                outcome.Batch.Skipped.Add(report);
                rows.Add(new
                {
                    viewportId = item.ViewportId,
                    actualMmX = currentMmX,
                    actualMmY = currentMmY,
                    status = "skipped"
                });
                continue;
            }

            try
            {
                var pointX = AgentSheetWriteHelper.MillimetersToInternal(targetMmX);
                var pointY = AgentSheetWriteHelper.MillimetersToInternal(targetMmY);
                viewport.SetBoxCenter(new XYZ(pointX, pointY, 0));
            }
            catch (Exception ex)
            {
                Fail(outcome, report, "move_failed",
                    "Revit refused to move viewport " + item.ViewportId + ": " + ex.Message);
                rows.Add(new { viewportId = item.ViewportId, status = "failed" });
                continue;
            }

            moved.Add(new PendingMove(viewport, report, currentMmX, currentMmY, targetMmX, targetMmY));
        }

        doc.Regenerate();

        foreach (var entry in moved)
        {
            ct.ThrowIfCancellationRequested();

            double actualMmX = entry.TargetMmX;
            double actualMmY = entry.TargetMmY;
            try
            {
                var actual = entry.Viewport.GetBoxCenter();
                actualMmX = AgentUnitsFormat.ToMillimeters(actual.X);
                actualMmY = AgentUnitsFormat.ToMillimeters(actual.Y);
            }
            catch (Exception)
            {
                // Keep the requested point as the best available read-back.
            }

            entry.Report.NewValue = AgentSheetWriteHelper.CenterText(actualMmX, actualMmY);
            outcome.Batch.Changed.Add(entry.Report);
            outcome.AffectedCount++;
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = entry.Viewport.UniqueId,
                ElementId = entry.Viewport.Id.GetValue(),
                ParameterName = "viewport_center",
                OldValue = AgentSheetWriteHelper.CenterText(entry.CurrentMmX, entry.CurrentMmY),
                NewValue = AgentSheetWriteHelper.CenterText(actualMmX, actualMmY),
                Status = ChangeStatus.Applied
            });
            rows.Add(new
            {
                viewportId = entry.Viewport.Id.GetValue(),
                requestedMmX = entry.TargetMmX,
                requestedMmY = entry.TargetMmY,
                actualMmX,
                actualMmY,
                status = "moved"
            });
        }

        outcome.Data["items"] = rows;
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

    private sealed record PendingMove(
        Viewport Viewport,
        WriteItemReport Report,
        double CurrentMmX,
        double CurrentMmY,
        double TargetMmX,
        double TargetMmY);

    private sealed class MoveViewportsRequest
    {
        public List<ViewportMoveItem>? Items { get; set; }

        public ShiftSpec? ShiftMm { get; set; }
    }

    private sealed class ViewportMoveItem
    {
        public long ViewportId { get; set; }

        public double? PointMmX { get; set; }

        public double? PointMmY { get; set; }
    }

    private sealed class ShiftSpec
    {
        public double Dx { get; set; }

        public double Dy { get; set; }
    }
}
