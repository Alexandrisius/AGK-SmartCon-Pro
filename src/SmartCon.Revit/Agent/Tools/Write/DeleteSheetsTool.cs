using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Deletes sheets by id. The report lists how many views and schedules were
/// placed on each sheet. Destructive: not reversible by undo_last.
/// </summary>
public sealed class DeleteSheetsTool : AgentWriteToolBase
{
    private const int MaxSheets = 50;

    public DeleteSheetsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "delete_sheets",
        Description =
            "Deletes sheets by sheetId (up to 50). Reports how many views were placed on each " +
            "sheet before deletion. Destructive: deletion cannot be undone by undo_last. " +
            "Dry run by default.",
        Risk = AgentToolRisk.Destructive,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sheetIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""sheetIds""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var sheetIds = AgentJson.GetLongArray(args, "sheetIds");
        if (sheetIds.Count == 0 || sheetIds.Count > MaxSheets)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "sheetIds must contain 1.." + MaxSheets + " ids.";
            return;
        }

        var deleted = 0;
        var failed = 0;
        foreach (var sheetId in sheetIds)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = sheetId };
            if (doc.GetElement(SmartCon.Core.Compatibility.ElementIdCompat.Create(sheetId))
                is not ViewSheet sheet)
            {
                report.ErrorCode = "sheet_not_found";
                report.Reason = "Element " + sheetId + " is not a sheet.";
                outcome.Batch.Failed.Add(report);
                failed++;
                continue;
            }

            var placedViews = 0;
            try
            {
                placedViews = sheet.GetAllPlacedViews().Count;
            }
            catch (Exception)
            {
                // The count is informational.
            }

            report.Name = sheet.SheetNumber + " - " + sheet.Name;
            report.Detail = placedViews + " placed view(s)";

            if (dryRun)
            {
                report.Reason = "Dry run: the sheet would be deleted.";
                outcome.Batch.Changed.Add(report);
                deleted++;
                continue;
            }

            try
            {
                doc.Delete(sheet.Id);
                report.NewValue = "deleted";
                outcome.Batch.Changed.Add(report);
                deleted++;
            }
            catch (Exception ex)
            {
                report.ErrorCode = "delete_failed";
                report.Reason = "Revit refused to delete sheet '" + sheet.SheetNumber + "': " +
                                ex.Message;
                outcome.Batch.Failed.Add(report);
                failed++;
            }
        }

        outcome.AffectedCount = deleted;
        if (failed > 0)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "partial_failure";
            outcome.Message = deleted + " sheet(s) deleted, " + failed + " failed. See failed[].";
        }

        if (!dryRun)
        {
            outcome.SuppressChangeSet = true;
            if (deleted > 0)
            {
                outcome.Data["undoSupported"] = false;
                outcome.Warnings.Add(
                    "Deleted sheets cannot be restored by undo_last; use the Revit Undo command " +
                    "or a backup if you need them back.");
            }
        }
    }
}