using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Removes a schedule placement from a sheet (deletes the
/// ScheduleSheetInstance — the schedule itself stays in the project, the
/// replacement-on-place flow of the rebuild: the archived schedule keeps its
/// setup but leaves the sheet). Undo re-creates the placement with the recorded
/// point and rotation.
/// </summary>
public sealed class RemoveScheduleFromSheetTool : AgentWriteToolBase
{
    public RemoveScheduleFromSheetTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "remove_schedule_from_sheet",
        Description =
            "Removes a schedule placement from a sheet: identify it by instanceId " +
            "(get_sheet_content / get_schedule_definition placements report it) or by " +
            "sheetId/sheetNumber + scheduleId/scheduleName. The schedule itself stays in " +
            "the project — only the placement (ScheduleSheetInstance) is deleted; this is " +
            "how the old schedule leaves the sheet in the rebuild flow (clone_schedule + " +
            "rename_schedule + place the new one at the same point). A schedule not placed " +
            "on the sheet fails with schedule_not_placed; revision schedules in titleblocks " +
            "are refused (schedule_not_removable). undo_last restores the placement with " +
            "its point and rotation. Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""instanceId"":{""type"":""integer""},""sheetId"":{""type"":""integer""},""sheetNumber"":{""type"":""string""},""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        ScheduleSheetInstance? instance;
        var instanceId = AgentJson.GetLong(args, "instanceId", 0);
        if (instanceId > 0)
        {
            instance = doc.GetElement(ElementIdCompat.Create(instanceId)) as ScheduleSheetInstance;
            if (instance is null)
            {
                outcome.Success = false;
                outcome.ErrorCode = "instance_not_found";
                outcome.Message = "Element " + instanceId +
                                  " is not a schedule placement. Use get_sheet_content or " +
                                  "get_schedule_definition placements to find the instanceId.";
                return;
            }
        }
        else
        {
            if (!AgentScheduleWriteHelper.TryResolveSchedule(
                    doc, args, out var schedule, out var scheduleError, out var scheduleMessage))
            {
                outcome.Success = false;
                outcome.ErrorCode = scheduleError;
                outcome.Message = scheduleMessage;
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

            instance = FindPlacement(doc, sheet!.Id, schedule!.Id);
            if (instance is null)
            {
                outcome.Success = false;
                outcome.ErrorCode = "schedule_not_placed";
                outcome.Message = "Schedule '" + schedule.Name + "' is not placed on sheet '" +
                                  sheet.SheetNumber + "'.";
                return;
            }
        }

        if (instance!.IsTitleblockRevisionSchedule)
        {
            outcome.Success = false;
            outcome.ErrorCode = "schedule_not_removable";
            outcome.Message = "The placement is a revision schedule in a titleblock family " +
                              "and is managed by Revit.";
            return;
        }

        double mmX;
        double mmY;
        try
        {
            var point = instance.Point;
            mmX = AgentUnitsFormat.ToMillimeters(point.X);
            mmY = AgentUnitsFormat.ToMillimeters(point.Y);
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "schedule_not_removable";
            outcome.Message = "The placement point is unreadable, so undo could not restore " +
                              "it: " + ex.Message;
            return;
        }

        ViewportRotation rotation;
        try
        {
            rotation = instance.Rotation;
        }
        catch (Exception)
        {
            rotation = ViewportRotation.None;
        }

        var scheduleName = ScheduleName(doc, instance.ScheduleId);
        // Capture everything before Delete: the element is invalid afterwards.
        var instanceUniqueId = instance.UniqueId;
        var instanceIdValue = instance.Id.GetValue();
        var scheduleIdValue = instance.ScheduleId.GetValue();
        var sheetIdValue = instance.OwnerViewId.GetValue();
        outcome.Description = "Remove schedule " + scheduleName + " from sheet";
        var report = new WriteItemReport
        {
            Id = instanceIdValue,
            Name = scheduleName,
            OldValue = AgentScheduleWriteHelper.SchedulePlacementText(
                scheduleIdValue,
                sheetIdValue,
                mmX,
                mmY,
                AgentScheduleWriteHelper.RotationName(rotation)),
            NewValue = "removed"
        };

        try
        {
            doc.Delete(instance.Id);
        }
        catch (Exception ex)
        {
            report.ErrorCode = "set_failed";
            report.Reason = "Revit refused to remove the placement: " +
                            ex.GetType().Name + ": " + ex.Message;
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.Message = report.Reason;
            return;
        }

        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = instanceUniqueId,
            ElementId = instanceIdValue,
            ParameterName = "schedule_unplaced",
            OldValue = report.OldValue,
            NewValue = "removed",
            Status = ChangeStatus.Applied
        });
        outcome.Data["instanceId"] = instanceIdValue;
        outcome.Data["scheduleId"] = scheduleIdValue;
        outcome.Data["sheetId"] = sheetIdValue;
    }

    private static ScheduleSheetInstance? FindPlacement(
        Document doc,
        ElementId sheetId,
        ElementId scheduleId)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
        {
            if (element is ScheduleSheetInstance instance
                && instance.OwnerViewId == sheetId
                && instance.ScheduleId == scheduleId)
                return instance;
        }

        return null;
    }

    private static string ScheduleName(Document doc, ElementId scheduleId)
    {
        var schedule = doc.GetElement(scheduleId);
        return schedule?.Name ?? scheduleId.GetValue().ToString();
    }
}
