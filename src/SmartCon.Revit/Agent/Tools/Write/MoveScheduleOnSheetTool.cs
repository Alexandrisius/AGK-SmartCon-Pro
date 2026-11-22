using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Moves a schedule placement on a sheet: sets ScheduleSheetInstance.Point (the
/// top-left corner of the table). Undo restores the previous point from the
/// schedule_moved change item.
/// </summary>
public sealed class MoveScheduleOnSheetTool : AgentWriteToolBase
{
    public MoveScheduleOnSheetTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "move_schedule_on_sheet",
        Description =
            "Moves a schedule already placed on a sheet: identify the placement by " +
            "instanceId (get_sheet_content reports it) or by sheetId/sheetNumber + " +
            "scheduleId/scheduleName. pointMmX/pointMmY are the new sheet coordinates " +
            "of the TOP-LEFT corner of the schedule table in millimetres from the sheet " +
            "bottom-left corner; the actual point is read back as actualMmX/actualMmY. " +
            "Optional rotation none|cw90|ccw90 turns the table (Revit supports only ±90°; " +
            "pinned placements and revision schedules are refused by Revit). The same " +
            "point and rotation is a no-op; a schedule not placed on the sheet fails with " +
            "schedule_not_on_sheet; revision schedules in titleblocks are refused " +
            "(schedule_not_movable). undo_last restores the previous position and " +
            "rotation. Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""instanceId"":{""type"":""integer""},""sheetId"":{""type"":""integer""},""sheetNumber"":{""type"":""string""},""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""pointMmX"":{""type"":""number""},""pointMmY"":{""type"":""number""},""rotation"":{""type"":""string"",""enum"":[""none"",""cw90"",""ccw90""],""description"":""New rotation of the table: 90° clockwise or counterclockwise""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""pointMmX"",""pointMmY""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        if (!AgentJson.TryGetDouble(args, "pointMmX", out var pointMmX)
            || !AgentJson.TryGetDouble(args, "pointMmY", out var pointMmY))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass pointMmX and pointMmY (top-left corner of the schedule " +
                              "table, sheet millimetres).";
            return;
        }

        var hasRotation = args.TryGetProperty("rotation", out var rotationElement)
                          && rotationElement.ValueKind == JsonValueKind.String;
        ViewportRotation requestedRotation = ViewportRotation.None;
        if (hasRotation
            && !AgentScheduleWriteHelper.TryParseRotation(
                rotationElement.GetString(), out requestedRotation, out var rotationReason))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = rotationReason!;
            return;
        }

        ScheduleSheetInstance? instance = null;
        var instanceId = AgentJson.GetLong(args, "instanceId", 0);
        if (instanceId > 0)
        {
            instance = doc.GetElement(ElementIdCompat.Create(instanceId)) as ScheduleSheetInstance;
            if (instance is null)
            {
                outcome.Success = false;
                outcome.ErrorCode = "instance_not_found";
                outcome.Message = "Element " + instanceId +
                                  " is not a schedule placement. Use get_sheet_content to find " +
                                  "the instanceId.";
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

            var sheetJson = args;
            if (!AgentSheetWriteHelper.TryResolveSheet(
                    doc, sheetJson, out var sheet, out _, out var sheetError, out var sheetMessage))
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
                outcome.ErrorCode = "schedule_not_on_sheet";
                outcome.Message = "Schedule '" + schedule.Name + "' is not placed on sheet '" +
                                  sheet.SheetNumber + "'. Use place_schedule_on_sheet first.";
                return;
            }
        }

        if (instance!.IsTitleblockRevisionSchedule)
        {
            outcome.Success = false;
            outcome.ErrorCode = "schedule_not_movable";
            outcome.Message = "The placement is a revision schedule in a titleblock family " +
                              "and cannot be moved.";
            return;
        }

        double oldMmX;
        double oldMmY;
        try
        {
            var oldPoint = instance.Point;
            oldMmX = AgentUnitsFormat.ToMillimeters(oldPoint.X);
            oldMmY = AgentUnitsFormat.ToMillimeters(oldPoint.Y);
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "schedule_not_movable";
            outcome.Message = "The placement point is unreadable: " + ex.Message;
            return;
        }

        var oldRotation = ViewportRotation.None;
        var rotationReadable = true;
        try
        {
            oldRotation = instance.Rotation;
        }
        catch (Exception)
        {
            rotationReadable = false;
        }

        outcome.Description = "Move schedule placement " + instance.Id.GetValue() + " to " +
                              AgentSheetWriteHelper.CenterText(pointMmX, pointMmY) + " mm";
        var rotationChanges = hasRotation && rotationReadable && oldRotation != requestedRotation;

        if (Math.Abs(oldMmX - pointMmX) < 0.5 && Math.Abs(oldMmY - pointMmY) < 0.5
            && !rotationChanges)
        {
            outcome.SuppressChangeSet = true;
            outcome.Data["instanceId"] = instance.Id.GetValue();
            outcome.Data["actualMmX"] = oldMmX;
            outcome.Data["actualMmY"] = oldMmY;
            if (hasRotation && !rotationReadable)
            {
                outcome.Message = "The schedule is already at this point; its rotation is " +
                                  "unreadable, so it was not changed.";
            }
            else
            {
                outcome.Data["rotation"] = AgentScheduleWriteHelper.RotationName(oldRotation);
                outcome.Message = "The schedule is already at this point and rotation (no-op).";
            }

            return;
        }

        try
        {
            var pointX = AgentSheetWriteHelper.MillimetersToInternal(pointMmX);
            var pointY = AgentSheetWriteHelper.MillimetersToInternal(pointMmY);
            instance.Point = new XYZ(pointX, pointY, 0);
            doc.Regenerate();
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "set_failed";
            outcome.Message = "Revit refused the new position: " +
                              ex.GetType().Name + ": " + ex.Message;
            return;
        }

        if (rotationChanges)
        {
            try
            {
                instance.Rotation = requestedRotation;
                doc.Regenerate();
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.ErrorCode = "set_failed";
                outcome.Message = "The point was moved, but Revit refused the rotation " +
                                  "(pinned placements and revision schedules cannot be " +
                                  "rotated): " + ex.GetType().Name + ": " + ex.Message;
                return;
            }
        }

        double actualMmX = pointMmX;
        double actualMmY = pointMmY;
        try
        {
            var actual = instance.Point;
            actualMmX = AgentUnitsFormat.ToMillimeters(actual.X);
            actualMmY = AgentUnitsFormat.ToMillimeters(actual.Y);
        }
        catch (Exception)
        {
            // The move succeeded even when the point is unreadable afterwards.
        }

        var scheduleName = ScheduleName(doc, instance.ScheduleId);
        outcome.AffectedCount = 1;
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = instance.Id.GetValue(),
            Name = scheduleName,
            OldValue = AgentSheetWriteHelper.CenterText(oldMmX, oldMmY),
            NewValue = AgentSheetWriteHelper.CenterText(actualMmX, actualMmY)
        });
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = instance.UniqueId,
            ElementId = instance.Id.GetValue(),
            ParameterName = "schedule_moved",
            OldValue = AgentSheetWriteHelper.CenterText(oldMmX, oldMmY),
            NewValue = AgentSheetWriteHelper.CenterText(actualMmX, actualMmY),
            Status = ChangeStatus.Applied
        });
        if (rotationChanges)
        {
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = instance.UniqueId,
                ElementId = instance.Id.GetValue(),
                ParameterName = "schedule_rotated",
                OldValue = AgentScheduleWriteHelper.RotationName(oldRotation),
                NewValue = AgentScheduleWriteHelper.RotationName(requestedRotation),
                Status = ChangeStatus.Applied
            });
        }

        outcome.Data["instanceId"] = instance.Id.GetValue();
        outcome.Data["scheduleId"] = instance.ScheduleId.GetValue();
        outcome.Data["sheetId"] = instance.OwnerViewId.GetValue();
        outcome.Data["requestedMmX"] = pointMmX;
        outcome.Data["requestedMmY"] = pointMmY;
        outcome.Data["actualMmX"] = actualMmX;
        outcome.Data["actualMmY"] = actualMmY;
        outcome.Data["rotation"] = SafeRotationName(instance);
    }

    private static string SafeRotationName(ScheduleSheetInstance instance)
    {
        try
        {
            return AgentScheduleWriteHelper.RotationName(instance.Rotation);
        }
        catch (Exception)
        {
            return "none";
        }
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
