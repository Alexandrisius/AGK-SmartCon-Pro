using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Moves a viewport to an absolute sheet position (the center of its box,
/// excluding the label) and reads the actual center back.
/// </summary>
public sealed class MoveViewportTool : AgentWriteToolBase
{
    public MoveViewportTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "move_viewport",
        Description =
            "Moves a viewport on a sheet to pointMmX/pointMmY — the new CENTER of the viewport " +
            "box (excluding the title label) in sheet millimetres from the bottom-left corner. " +
            "The actual center is read back after the move and returned. Take viewportId and the " +
            "current coordinates from get_sheet_content. Idempotent: moving to the current " +
            "center is a no-op; dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewportId"":{""type"":""integer""},""pointMmX"":{""type"":""number"",""description"":""New viewport center X in mm from the sheet bottom-left corner""},""pointMmY"":{""type"":""number"",""description"":""New viewport center Y in mm from the sheet bottom-left corner""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""viewportId"",""pointMmX"",""pointMmY""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var viewportId = AgentJson.GetLong(args, "viewportId", 0);
        if (doc.GetElement(ElementIdCompat.Create(viewportId)) is not Viewport viewport)
        {
            outcome.Success = false;
            outcome.ErrorCode = "viewport_not_found";
            outcome.Message = "Element " + viewportId + " is not a viewport.";
            return;
        }

        if (!AgentJson.TryGetDouble(args, "pointMmX", out var mmX) ||
            !AgentJson.TryGetDouble(args, "pointMmY", out var mmY))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "pointMmX and pointMmY are required (millimetres).";
            return;
        }

        var sheet = doc.GetElement(viewport.SheetId) as ViewSheet;
        outcome.Description = "Move viewport " + viewportId + " on sheet " +
                              (sheet is null ? "?" : sheet.SheetNumber);

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
            outcome.Success = false;
            outcome.ErrorCode = "viewport_not_placeable";
            outcome.Message = "The viewport has no box center on a sheet: " + ex.Message;
            return;
        }

        var report = new WriteItemReport
        {
            Id = viewportId,
            Name = sheet is null ? null : sheet.SheetNumber + " viewport " + viewportId,
            OldValue = AgentSheetWriteHelper.CenterText(currentMmX, currentMmY)
        };

        if (Math.Abs(currentMmX - mmX) < 0.05 && Math.Abs(currentMmY - mmY) < 0.05)
        {
            report.Reason = "The viewport is already at this center (no-op).";
            outcome.Batch.Skipped.Add(report);
            return;
        }

        try
        {
            var pointX = AgentSheetWriteHelper.MillimetersToInternal(mmX);
            var pointY = AgentSheetWriteHelper.MillimetersToInternal(mmY);
            viewport.SetBoxCenter(new XYZ(pointX, pointY, 0));
            doc.Regenerate();
        }
        catch (Exception ex)
        {
            report.ErrorCode = "move_failed";
            report.Reason = "Revit refused to move viewport " + viewportId + ": " + ex.Message;
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            return;
        }

        double actualMmX = mmX;
        double actualMmY = mmY;
        try
        {
            var actual = viewport.GetBoxCenter();
            actualMmX = AgentUnitsFormat.ToMillimeters(actual.X);
            actualMmY = AgentUnitsFormat.ToMillimeters(actual.Y);
        }
        catch (Exception)
        {
            // Keep the requested point as the best available read-back.
        }

        report.NewValue = AgentSheetWriteHelper.CenterText(actualMmX, actualMmY);
        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount++;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = viewport.UniqueId,
            ElementId = viewport.Id.GetValue(),
            ParameterName = "viewport_center",
            OldValue = AgentSheetWriteHelper.CenterText(currentMmX, currentMmY),
            NewValue = AgentSheetWriteHelper.CenterText(actualMmX, actualMmY),
            Status = ChangeStatus.Applied
        });
        outcome.Data["viewportId"] = viewportId;
        outcome.Data["actualMmX"] = actualMmX;
        outcome.Data["actualMmY"] = actualMmY;
    }
}
