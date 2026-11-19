using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Deletes regular views (not sheets, not templates unless explicitly allowed).
/// Destructive: undo_last cannot recreate a deleted view, so no change set is
/// recorded and the answer says so.
/// </summary>
public sealed class DeleteViewsTool : AgentWriteToolBase
{
    private const int MaxViews = 50;

    public DeleteViewsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "delete_views",
        Description =
            "Deletes views by id (up to 50). Sheets are refused — use delete_sheets; view " +
            "templates are refused unless includeTemplates=true. Destructive: deletion cannot " +
            "be undone by undo_last (use the Revit Undo command or a backup). Dry run by default.",
        Risk = AgentToolRisk.Destructive,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""}},""includeTemplates"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""viewIds""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var viewIds = AgentJson.GetLongArray(args, "viewIds");
        if (viewIds.Count == 0 || viewIds.Count > MaxViews)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "viewIds must contain 1.." + MaxViews + " ids.";
            return;
        }

        var includeTemplates = AgentJson.GetBool(args, "includeTemplates", false);
        var deleted = 0;
        var failed = 0;

        foreach (var viewId in viewIds)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = viewId };
            if (!AgentViewWriteHelper.TryGetView(
                    doc, viewId, out var view, out var code, out var reason))
            {
                report.ErrorCode = code;
                report.Reason = reason;
                outcome.Batch.Failed.Add(report);
                failed++;
                continue;
            }

            report.Name = view.Name;

            if (view is ViewSheet)
            {
                report.ErrorCode = "use_delete_sheets";
                report.Reason = "View " + viewId + " is a sheet; use delete_sheets.";
                outcome.Batch.Failed.Add(report);
                failed++;
                continue;
            }

            if (view.IsTemplate && !includeTemplates)
            {
                report.ErrorCode = "view_is_template";
                report.Reason = "View " + viewId + " is a view template; pass includeTemplates=true " +
                                "to delete it.";
                outcome.Batch.Failed.Add(report);
                failed++;
                continue;
            }

            if (dryRun)
            {
                report.Reason = "Dry run: the view would be deleted.";
                outcome.Batch.Changed.Add(report);
                deleted++;
                continue;
            }

            try
            {
                doc.Delete(view.Id);
                report.NewValue = "deleted";
                outcome.Batch.Changed.Add(report);
                deleted++;
            }
            catch (Exception ex)
            {
                report.ErrorCode = "delete_failed";
                report.Reason = "Revit refused to delete view '" + view.Name + "': " + ex.Message;
                outcome.Batch.Failed.Add(report);
                failed++;
            }
        }

        outcome.AffectedCount = deleted;
        if (failed > 0)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "partial_failure";
            outcome.Message = deleted + " view(s) deleted, " + failed + " failed. See failed[].";
        }

        if (!dryRun)
        {
            outcome.SuppressChangeSet = true;
            if (deleted > 0)
            {
                outcome.Data["undoSupported"] = false;
                outcome.Warnings.Add(
                    "Deleted views cannot be restored by undo_last; use the Revit Undo command " +
                    "or a backup if you need them back.");
            }
        }
    }
}