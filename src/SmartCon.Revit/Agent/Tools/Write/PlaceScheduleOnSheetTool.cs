using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Places a schedule on a sheet (ScheduleSheetInstance, no viewport). The
/// point is the top-left corner of the table in sheet millimetres; a schedule
/// already on the sheet is skipped (no-op — Revit allows repeats, the agent
/// does not).
/// </summary>
public sealed class PlaceScheduleOnSheetTool : AgentWriteToolBase
{
    public PlaceScheduleOnSheetTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "place_schedule_on_sheet",
        Description =
            "Places a schedule on a sheet by scheduleId or exact scheduleName and " +
            "sheetId/sheetNumber. pointMmX/pointMmY are the sheet coordinates of the " +
            "TOP-LEFT corner of the schedule table in millimetres from the sheet bottom-left " +
            "corner (get_sheet_content reports the same point as xMm/yMm); the actual point " +
            "is read back as actualMmX/actualMmY. A schedule already placed on this sheet " +
            "is skipped (no-op). Schedules have no viewport — they are placed as " +
            "ScheduleSheetInstance. Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""sheetId"":{""type"":""integer""},""sheetNumber"":{""type"":""string""},""pointMmX"":{""type"":""number"",""description"":""Top-left corner X in mm from the sheet bottom-left corner""},""pointMmY"":{""type"":""number"",""description"":""Top-left corner Y in mm from the sheet bottom-left corner""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""pointMmX"",""pointMmY""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        if (!AgentScheduleWriteHelper.TryResolveSchedule(
                doc, args, out var schedule, out var errorCode, out var message))
        {
            outcome.Success = false;
            outcome.ErrorCode = errorCode;
            outcome.Message = message;
            return;
        }

        if (schedule!.IsTemplate)
        {
            outcome.Success = false;
            outcome.ErrorCode = "schedule_is_template";
            outcome.Message = "Schedule '" + schedule.Name + "' is a template and cannot be " +
                              "placed on a sheet.";
            return;
        }

        if (!AgentSheetWriteHelper.TryResolveSheet(
                doc, args, out var sheet, out _, out var sheetError, out var sheetMessage))
        {
            outcome.Success = false;
            outcome.ErrorCode = sheetError;
            outcome.Message = sheetMessage;
            return;
        }

        if (!AgentJson.TryGetDouble(args, "pointMmX", out var pointMmX)
            || !AgentJson.TryGetDouble(args, "pointMmY", out var pointMmY))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass pointMmX and pointMmY (top-left corner of the schedule " +
                              "table, sheet millimetres).";
            return;
        }

        outcome.Description = "Place schedule " + schedule.Name + " on sheet " +
                              sheet!.SheetNumber;

        var report = new WriteItemReport
        {
            Id = schedule.Id.GetValue(),
            Name = schedule.Name
        };

        if (IsPlacedOnSheet(doc, sheet.Id, schedule.Id))
        {
            report.Reason = "The schedule is already placed on this sheet (no-op).";
            outcome.Batch.Skipped.Add(report);
            return;
        }

        ScheduleSheetInstance instance;
        try
        {
            var pointX = AgentSheetWriteHelper.MillimetersToInternal(pointMmX);
            var pointY = AgentSheetWriteHelper.MillimetersToInternal(pointMmY);
            doc.Regenerate();
            instance = ScheduleSheetInstance.Create(
                doc, sheet.Id, schedule.Id, new XYZ(pointX, pointY, 0));
            doc.Regenerate();
        }
        catch (Exception ex)
        {
            report.ErrorCode = "schedule_not_placeable";
            report.Reason = "Revit refused to place '" + schedule.Name + "' on sheet '" +
                            sheet.SheetNumber + "': " + ex.Message;
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.Message = report.Reason;
            return;
        }

        double actualMmX = pointMmX;
        double actualMmY = pointMmY;
        try
        {
            var point = instance.Point;
            actualMmX = AgentUnitsFormat.ToMillimeters(point.X);
            actualMmY = AgentUnitsFormat.ToMillimeters(point.Y);
        }
        catch (Exception)
        {
            // The placed instance is valid even when the point is unreadable.
        }

        report.NewValue = AgentSheetWriteHelper.CenterText(actualMmX, actualMmY);
        report.Detail = "Requested top-left " +
                        AgentSheetWriteHelper.CenterText(pointMmX, pointMmY) + " mm";
        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount++;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = instance.UniqueId,
            ElementId = instance.Id.GetValue(),
            ParameterName = "schedule_placed",
            NewValue = schedule.Name,
            Status = ChangeStatus.Applied
        });
        outcome.Data["scheduleId"] = schedule.Id.GetValue();
        outcome.Data["sheetId"] = sheet.Id.GetValue();
        outcome.Data["instanceId"] = instance.Id.GetValue();
        outcome.Data["requestedMmX"] = pointMmX;
        outcome.Data["requestedMmY"] = pointMmY;
        outcome.Data["actualMmX"] = actualMmX;
        outcome.Data["actualMmY"] = actualMmY;
    }

    private static bool IsPlacedOnSheet(Document doc, ElementId sheetId, ElementId scheduleId)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
        {
            if (element is not ScheduleSheetInstance instance)
                continue;

            if (instance.OwnerViewId == sheetId && instance.ScheduleId == scheduleId)
                return true;
        }

        return false;
    }
}
