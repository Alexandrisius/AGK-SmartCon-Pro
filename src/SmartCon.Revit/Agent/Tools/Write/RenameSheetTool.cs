using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>Renames one sheet; sheet names are not unique in Revit.</summary>
public sealed class RenameSheetTool : AgentWriteToolBase
{
    public RenameSheetTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "rename_sheet",
        Description =
            "Renames one sheet, identified by sheetId, sheetNumber or sheetName. Sheet names are " +
            "not unique in Revit, so several sheets may share a name after the rename. " +
            "Idempotent: assigning the current name is a no-op; dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sheetId"":{""type"":""integer""},""sheetNumber"":{""type"":""string"",""description"":""Current number, when sheetId is unknown""},""sheetName"":{""type"":""string"",""description"":""Current name, when sheetId/number are unknown""},""newName"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""newName""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var newName = AgentJson.GetString(args, "newName");
        if (string.IsNullOrWhiteSpace(newName))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass newName (the new sheet name).";
            return;
        }

        if (!AgentSheetWriteHelper.TryResolveSheet(
                doc, args, out var sheet, out var candidates, out var errorCode, out var message))
        {
            outcome.Success = false;
            outcome.ErrorCode = errorCode;
            outcome.Message = message!;
            if (candidates.Count > 0)
                outcome.Data["candidates"] = candidates;
            return;
        }

        var current = sheet!.Name ?? string.Empty;
        outcome.Description = "Rename sheet " + sheet.SheetNumber;

        var report = new WriteItemReport
        {
            Id = sheet.Id.GetValue(),
            Name = AgentSheetWriteHelper.SheetLabel(sheet),
            OldValue = current
        };

        if (string.Equals(current, newName, StringComparison.Ordinal))
        {
            report.Reason = "The sheet already has this name (no-op).";
            outcome.Batch.Skipped.Add(report);
            return;
        }

        try
        {
            sheet.Name = newName!.Trim();
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_name";
            outcome.Message = "Revit refused the sheet name '" + newName + "': " + ex.Message;
            return;
        }

        report.NewValue = sheet.Name;
        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount++;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = sheet.UniqueId,
            ElementId = sheet.Id.GetValue(),
            ParameterName = "sheet_name",
            OldValue = current,
            NewValue = sheet.Name,
            Status = ChangeStatus.Applied
        });
        outcome.Data["sheetId"] = sheet.Id.GetValue();
        outcome.Data["name"] = sheet.Name;
    }
}
