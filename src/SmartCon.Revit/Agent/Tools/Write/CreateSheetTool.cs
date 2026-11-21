using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Creates a sheet with a unique number, an optional name and a title block
/// type. Not idempotent: a repeated create with the same number returns
/// sheet_number_taken.
/// </summary>
public sealed class CreateSheetTool : AgentWriteToolBase
{
    public CreateSheetTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_sheet",
        Description =
            "Creates a new sheet. number is required and must be unique (a number that only " +
            "differs by invisible characters counts as taken); name is optional. titleBlockTypeId " +
            "selects a loaded title block type (FamilySymbol of OST_TitleBlocks); without it the " +
            "first loaded type is used. When the project has no title block the tool returns " +
            "title_block_missing — load a title block family first. Returns sheetId, number, name " +
            "and the title block name. Not idempotent; dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""number"":{""type"":""string"",""description"":""Unique sheet number, e.g. AR-101""},""name"":{""type"":""string"",""description"":""Sheet name, e.g. Plan of 1st floor""},""titleBlockTypeId"":{""type"":""integer"",""description"":""Title block type id from list_sheets/get_project_overview; default: first loaded""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""number""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var number = AgentJson.GetString(args, "number");
        if (string.IsNullOrWhiteSpace(number))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass number (the new sheet number, e.g. \"AR-101\").";
            return;
        }

        var invalidReason = AgentSheetWriteHelper.ValidateNumber(number);
        if (invalidReason is not null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "sheet_number_invalid";
            outcome.Message = invalidReason;
            return;
        }

        var requestedTypeId = AgentJson.GetLong(args, "titleBlockTypeId", 0);
        ElementId? titleBlockId;
        if (requestedTypeId > 0)
        {
            titleBlockId = AgentSheetWriteHelper.FindTitleBlockType(
                doc, requestedTypeId, out var typeError, out var typeMessage);
            if (titleBlockId is null)
            {
                outcome.Success = false;
                outcome.ErrorCode = typeError!;
                outcome.Message = typeMessage;
                return;
            }
        }
        else
        {
            var available = AgentSheetWriteHelper.CollectTitleBlockTypeIds(doc);
            if (available.Count == 0)
            {
                outcome.Success = false;
                outcome.ErrorCode = "title_block_missing";
                outcome.Message =
                    "The project has no loaded title block type, and ViewSheet.Create needs " +
                    "one. Load a title block family (a .rfa of OST_TitleBlocks) and retry.";
                return;
            }

            titleBlockId = available[0];
        }

        var owner = AgentSheetWriteHelper.FindNumberOwner(doc, number!, 0);
        if (owner is not null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "sheet_number_taken";
            outcome.Message = "Sheet number '" + number + "' is already used by sheet " +
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

        var name = AgentJson.GetString(args, "name");
        var sheet = ViewSheet.Create(doc, titleBlockId);
        sheet.SheetNumber = number!;
        if (!string.IsNullOrWhiteSpace(name))
            sheet.Name = name!.Trim();

        var titleBlockName = (doc.GetElement(titleBlockId) as FamilySymbol)?.Name;
        var sheetId = sheet.Id.GetValue();
        var appliedNumber = sheet.SheetNumber;
        var appliedName = sheet.Name;

        outcome.Description = "Create sheet " + appliedNumber;
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = sheetId,
            Name = appliedNumber + " - " + appliedName,
            NewValue = appliedNumber,
            Detail = "Title block: " + titleBlockName
        });
        outcome.AffectedCount++;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = sheet.UniqueId,
            ElementId = sheetId,
            ParameterName = "sheet_created",
            NewValue = appliedNumber,
            Status = ChangeStatus.Applied
        });
        outcome.Data["sheet"] = new
        {
            sheetId,
            number = appliedNumber,
            name = appliedName,
            titleBlockTypeId = titleBlockId!.GetValue(),
            titleBlockName
        };
    }
}
