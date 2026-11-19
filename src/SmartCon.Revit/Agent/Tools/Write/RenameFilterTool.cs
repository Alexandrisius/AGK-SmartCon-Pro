using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Renames a parameter view filter. Unlike rule replacement and deletion the rename
/// is reversible: undo_last restores the previous name through the change set.
/// </summary>
public sealed class RenameFilterTool : AgentWriteToolBase
{
    private const int MaxKnownNames = 20;

    public RenameFilterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "rename_filter",
        Description =
            "Renames a parameter view filter by filterId or filterName. The old name is " +
            "recorded in the change set, so undo_last restores it. Refuses a name that another " +
            "filter already uses. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""filterId"":{""type"":""integer""},""filterName"":{""type"":""string""},""newName"":{""type"":""string"",""minLength"":1},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""newName""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var filterId = AgentJson.GetLong(args, "filterId", 0);
        var filterName = AgentJson.GetString(args, "filterName");
        var filter = filterId > 0
            ? AgentFilterWriteHelper.FindById(doc, filterId)
            : null;
        if (filter is null && !string.IsNullOrWhiteSpace(filterName))
            filter = AgentFilterWriteHelper.FindByName(doc, filterName);

        if (filter is null)
        {
            Fail(
                outcome,
                "filter_not_found",
                "Pass filterId or filterName of an existing view filter (see list_filters).");
            return;
        }

        var newName = AgentJson.GetString(args, "newName");
        if (string.IsNullOrWhiteSpace(newName))
        {
            Fail(outcome, "invalid_arguments", "newName is required and cannot be empty.");
            return;
        }

        newName = newName!.Trim();
        var oldName = filter.Name;
        var report = new WriteItemReport
        {
            Id = filter.Id.GetValue(),
            Name = oldName,
            OldValue = oldName,
            NewValue = newName
        };

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            report.Reason = "The filter already has this name (no-op).";
            outcome.Batch.Skipped.Add(report);
            outcome.Message = "No changes: the filter already has the requested name.";
            return;
        }

        var conflict = AgentFilterWriteHelper.FindByName(doc, newName);
        if (conflict is not null
            && conflict.Id.GetValue() != filter.Id.GetValue())
        {
            outcome.Data["knownFilters"] = AgentFilterWriteHelper.KnownFilterNames(
                doc, MaxKnownNames);
            outcome.Batch.Failed.Add(report);
            report.ErrorCode = "duplicate_name";
            report.Reason = "Another filter already has the name '" + newName + "' (id " +
                            conflict.Id.GetValue() + ").";
            Fail(outcome, "duplicate_name", report.Reason);
            return;
        }

        if (dryRun)
        {
            report.Reason = "Dry run: the filter would be renamed.";
            outcome.Batch.Changed.Add(report);
            outcome.Data["wouldRename"] = true;
            return;
        }

        try
        {
            filter.Name = newName;
        }
        catch (Exception ex)
        {
            report.ErrorCode = "rename_failed";
            report.Reason = "Revit refused to rename the filter: " + ex.Message;
            outcome.Batch.Failed.Add(report);
            Fail(outcome, "rename_failed", report.Reason);
            return;
        }

        if (!string.Equals(filter.Name, newName, StringComparison.Ordinal))
        {
            report.ErrorCode = "rename_failed";
            report.Reason = "The filter name did not change as expected.";
            outcome.Batch.Failed.Add(report);
            Fail(outcome, "rename_failed", report.Reason);
            return;
        }

        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount = 1;
        outcome.Description = "Renamed filter '" + oldName + "' to '" + newName + "'";
        outcome.Items.Add(new ChangeItem
        {
            ElementId = filter.Id.GetValue(),
            UniqueId = filter.UniqueId,
            ParameterName = "filter_name",
            OldValue = oldName,
            NewValue = newName,
            Status = ChangeStatus.Applied
        });
    }

    private static void Fail(WriteOutcome outcome, string errorCode, string message)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = message;
    }
}