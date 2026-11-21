using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Changes the number of one sheet. The number is validated up front (empty,
/// forbidden characters, invisible code units) and checked against raw and
/// visual duplicates, so Revit never has to throw.
/// </summary>
public sealed class SetSheetNumberTool : AgentWriteToolBase
{
    public SetSheetNumberTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_sheet_number",
        Description =
            "Changes the number of one sheet, identified by sheetId, sheetNumber or sheetName. " +
            "newNumber must be non-empty, free of the characters Revit refuses ({ } [ ] | ; < > ? ~) " +
            "and of invisible code units (assign the visible form instead). A number already used " +
            "by another sheet — including one that only differs by invisible characters — returns " +
            "sheet_number_taken with the owner. Idempotent: assigning the current number is a " +
            "no-op; dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sheetId"":{""type"":""integer""},""sheetNumber"":{""type"":""string"",""description"":""Current number, when sheetId is unknown""},""sheetName"":{""type"":""string"",""description"":""Current name, when sheetId/number are unknown""},""newNumber"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""newNumber""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var newNumber = AgentJson.GetString(args, "newNumber");
        if (string.IsNullOrWhiteSpace(newNumber))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass newNumber (the new sheet number).";
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

        var invalidReason = AgentSheetWriteHelper.ValidateNumber(newNumber);
        if (invalidReason is not null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "sheet_number_invalid";
            outcome.Message = invalidReason;
            return;
        }

        var current = sheet!.SheetNumber ?? string.Empty;
        outcome.Description = "Renumber sheet " + current + " to " + newNumber;

        var report = new WriteItemReport
        {
            Id = sheet.Id.GetValue(),
            Name = AgentSheetWriteHelper.SheetLabel(sheet),
            OldValue = current
        };

        if (string.Equals(current, newNumber, StringComparison.Ordinal))
        {
            report.Reason = "The sheet already has this number (no-op).";
            outcome.Batch.Skipped.Add(report);
            return;
        }

        var owner = AgentSheetWriteHelper.FindNumberOwner(doc, newNumber!, sheet.Id.GetValue());
        if (owner is not null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "sheet_number_taken";
            outcome.Message = "Sheet number '" + newNumber + "' is already used by sheet " +
                              AgentSheetWriteHelper.SheetLabel(owner) + " (id " +
                              owner.Id.GetValue() + "). Choose another number.";
            outcome.Data["owner"] = new
            {
                sheetId = owner.Id.GetValue(),
                number = owner.SheetNumber,
                name = owner.Name
            };
            return;
        }

        sheet.SheetNumber = newNumber!;
        report.NewValue = sheet.SheetNumber;
        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount++;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = sheet.UniqueId,
            ElementId = sheet.Id.GetValue(),
            ParameterName = "sheet_number",
            OldValue = current,
            NewValue = sheet.SheetNumber,
            Status = ChangeStatus.Applied
        });
        outcome.Data["sheetId"] = sheet.Id.GetValue();
        outcome.Data["number"] = sheet.SheetNumber;
    }
}
