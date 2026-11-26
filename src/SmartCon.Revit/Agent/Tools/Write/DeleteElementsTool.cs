using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Deletes model elements by id. The dry run resolves each id and reports
/// name, category and dependent-element count; the apply requires
/// confirmNames equal to a fresh recomputation of the same list (S.5), which
/// also refuses the call when the model changed between the two calls.
/// </summary>
public sealed class DeleteElementsTool : AgentWriteToolBase
{
    private const int MaxElements = 200;

    public DeleteElementsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "delete_elements",
        Description =
            "Deletes model elements by elementId (up to 200). The dry run resolves every id " +
            "and returns names[], per-item category and dependentsCount (elements Revit will " +
            "delete together with it). Apply requires confirmNames equal to a FRESH dry-run " +
            "names[] list: a mismatch (model changed) refuses the call. Destructive: deletion " +
            "cannot be undone by undo_last. Dry run by default.",
        Risk = AgentToolRisk.Destructive,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""integer""}},""confirmNames"":{""type"":""array"",""items"":{""type"":""string""}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""elementIds""],""additionalProperties"":false}"
    };

    private sealed class Target : AgentDeleteHelper.DeleteTarget
    {
        public string Category { get; init; } = string.Empty;

        public int Dependents { get; init; }
    }

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var elementIds = AgentJson.GetLongArray(args, "elementIds");
        if (elementIds.Count == 0 || elementIds.Count > MaxElements)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "elementIds must contain 1.." + MaxElements + " ids.";
            return;
        }

        var seen = new HashSet<long>();
        var targets = new List<Target>(elementIds.Count);
        foreach (var elementId in elementIds)
        {
            ct.ThrowIfCancellationRequested();

            if (!seen.Add(elementId))
                continue;

            var report = new WriteItemReport { Id = elementId };
            var element = doc.GetElement(ElementIdCompat.Create(elementId));
            if (element is null)
            {
                report.ErrorCode = "element_not_found";
                report.Reason = "Element " + elementId + " does not exist in the active document.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var dependents = 0;
            try
            {
                dependents = element.GetDependentElements(null).Count;
            }
            catch (Exception)
            {
                // The count is informational.
            }

            targets.Add(new Target
            {
                Id = elementId,
                Name = element.Name ?? string.Empty,
                Category = element.Category?.Name ?? string.Empty,
                Dependents = dependents
            });
        }

        var names = targets.Select(target => target.Name).ToArray();
        AgentDeleteHelper.PublishNames(outcome, names);

        var items = new List<object>(targets.Count);
        foreach (var target in targets)
        {
            items.Add(new
            {
                elementId = target.Id,
                name = target.Name,
                category = target.Category,
                dependentsCount = target.Dependents
            });
        }

        outcome.Data["items"] = items;

        if (dryRun)
        {
            foreach (var target in targets)
            {
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    Detail = target.Category + ", " + target.Dependents + " dependent(s)",
                    Reason = "Dry run: the element would be deleted."
                });
            }

            outcome.AffectedCount = targets.Count;
            return;
        }

        var confirm = AgentDeleteConfirm.Verify(names, AgentDeleteHelper.GetConfirmNames(args));
        if (!confirm.Ok)
        {
            AgentDeleteHelper.RefuseConfirmation(outcome, confirm);
            return;
        }

        var deletedCount = AgentDeleteHelper.DeleteBatch(
            doc,
            targets.Cast<AgentDeleteHelper.DeleteTarget>().ToList(),
            outcome,
            "elements",
            ct,
            WorksharingScope);
        outcome.AffectedCount = deletedCount;

        if (outcome.Batch.Failed.Count > 0)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "partial_failure";
            outcome.Message = deletedCount + " element(s) deleted, " +
                              outcome.Batch.Failed.Count + " failed. See failed[].";
        }

        if (deletedCount > 0)
            AgentDeleteHelper.MarkDestructiveApplied(outcome, "elements");
        else
            outcome.SuppressChangeSet = true;
    }
}
