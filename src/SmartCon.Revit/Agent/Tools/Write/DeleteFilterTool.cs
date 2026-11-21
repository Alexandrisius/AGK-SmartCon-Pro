using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Deletes a parameter view filter. Removing a filter cannot be reverted by
/// undo_last (the projects the filter was applied to also lose it), so the tool
/// runs as a destructive operation without a change set.
/// </summary>
public sealed class DeleteFilterTool : AgentWriteToolBase
{
    public DeleteFilterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "delete_filter",
        Description =
            "Deletes a parameter view filter by filterId or filterName and reports the views it " +
            "was applied to (includeAppliedViews=false skips that scan). The dry run returns " +
            "names[]; apply requires confirmNames equal to a FRESH dry-run names[] list (a " +
            "model change refuses the call). Deletion is destructive and cannot be undone by " +
            "undo_last; use the Revit Undo command or a backup if the filter is needed again. " +
            "Dry run by default.",
        Risk = AgentToolRisk.Destructive,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""filterId"":{""type"":""integer""},""filterName"":{""type"":""string""},""includeAppliedViews"":{""type"":""boolean"",""default"":true},""confirmNames"":{""type"":""array"",""items"":{""type"":""string""}},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
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
            outcome.Success = false;
            outcome.ErrorCode = "filter_not_found";
            outcome.Message = "Pass filterId or filterName of an existing view filter " +
                              "(see list_filters).";
            return;
        }

        // Cache before Delete: reading Name after the element is gone throws
        // InvalidObjectException (stress-test regression A).
        var resolvedName = filter.Name;
        var resolvedId = filter.Id.GetValue();

        var includeAppliedViews = AgentJson.GetBool(args, "includeAppliedViews", true);
        int? appliedViewCount = null;
        if (includeAppliedViews)
        {
            var usage = AgentFilterUsage.Map(doc, ct);
            appliedViewCount = AgentFilterUsage.Count(usage, resolvedId);
        }

        var report = new WriteItemReport
        {
            Id = resolvedId,
            Name = resolvedName,
            OldValue = appliedViewCount.HasValue
                ? appliedViewCount.Value + " view(s)"
                : "notComputed"
        };

        if (dryRun)
        {
            report.Reason = "Dry run: the filter would be deleted" +
                            (appliedViewCount is > 0
                                ? " and removed from " + appliedViewCount.Value + " view(s)."
                                : ".");
            outcome.Batch.Changed.Add(report);
            outcome.Data["wouldDelete"] = true;
            outcome.Data["names"] = new List<string> { resolvedName };
            outcome.Data["appliedViewCount"] = appliedViewCount.HasValue
                ? (object)appliedViewCount.Value
                : "notComputed";
            outcome.Data["undoSupported"] = false;
            return;
        }

        var confirm = AgentDeleteConfirm.Verify(
            new[] { resolvedName },
            AgentDeleteHelper.GetConfirmNames(args));
        if (!confirm.Ok)
        {
            AgentDeleteHelper.RefuseConfirmation(outcome, confirm);
            return;
        }

        try
        {
            doc.Delete(filter.Id);
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "delete_failed";
            outcome.Message = "Revit refused to delete filter '" + resolvedName + "': " + ex.Message;
            return;
        }

        report.NewValue = "deleted";
        report.Detail = appliedViewCount is > 0
            ? "removed from " + appliedViewCount.Value + " view(s)"
            : appliedViewCount.HasValue
                ? "not applied to any view"
                : "applied-view count was not computed";
        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount = 1;
        outcome.Data["appliedViewCount"] = appliedViewCount.HasValue
            ? (object)appliedViewCount.Value
            : "notComputed";
        outcome.Data["undoSupported"] = false;
        outcome.SuppressChangeSet = true;
        outcome.Warnings.Add(
            "Filter deleted. undo_last cannot restore a deleted filter" +
            (appliedViewCount is > 0
                ? " (it was removed from " + appliedViewCount.Value + " view(s))"
                : string.Empty) +
            "; use the Revit Undo command or a backup if you need it back.");
    }
}