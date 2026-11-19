using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Deletes family types (FamilySymbol). A type in use is refused unless
/// force=true, because Revit then also deletes its instances.
/// </summary>
public sealed class DeleteFamilyTypesTool : AgentWriteToolBase
{
    private const int MaxTypes = 50;

    public DeleteFamilyTypesTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "delete_family_types",
        Description =
            "Deletes family types (FamilySymbol) by typeId (up to 50). A type with placed " +
            "instances is refused unless force=true; forced deletion also deletes those " +
            "instances. Destructive: not reversible by undo_last. Dry run by default.",
        Risk = AgentToolRisk.Destructive,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""typeIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""}},""force"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""typeIds""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var typeIds = AgentJson.GetLongArray(args, "typeIds");
        if (typeIds.Count == 0 || typeIds.Count > MaxTypes)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "typeIds must contain 1.." + MaxTypes + " ids.";
            return;
        }

        var force = AgentJson.GetBool(args, "force", false);
        var requested = new HashSet<long>(typeIds);
        var instanceCounts = CountInstances(doc, requested, ct);

        var deleted = 0;
        var failed = 0;
        foreach (var typeId in typeIds)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = typeId };
            if (doc.GetElement(SmartCon.Core.Compatibility.ElementIdCompat.Create(typeId))
                is not FamilySymbol symbol)
            {
                report.ErrorCode = "type_not_found";
                report.Reason = "Element " + typeId + " is not a family type.";
                outcome.Batch.Failed.Add(report);
                failed++;
                continue;
            }

            report.Name = (symbol.Family?.Name ?? "?") + " : " + symbol.Name;
            instanceCounts.TryGetValue(typeId, out var instances);
            report.Detail = instances + " placed instance(s)";

            if (instances > 0 && !force)
            {
                report.ErrorCode = "type_in_use";
                report.Reason = "The type has " + instances + " placed instance(s); pass force=true " +
                                "to delete the type together with its instances.";
                outcome.Batch.Failed.Add(report);
                failed++;
                continue;
            }

            if (dryRun)
            {
                report.Reason = "Dry run: the type would be deleted" +
                                (instances > 0 ? " with " + instances + " instance(s)." : ".");
                outcome.Batch.Changed.Add(report);
                deleted++;
                continue;
            }

            try
            {
                doc.Delete(symbol.Id);
                report.NewValue = "deleted";
                outcome.Batch.Changed.Add(report);
                deleted++;
            }
            catch (Exception ex)
            {
                report.ErrorCode = "delete_failed";
                report.Reason = "Revit refused to delete type '" + symbol.Name + "': " + ex.Message;
                outcome.Batch.Failed.Add(report);
                failed++;
            }
        }

        outcome.AffectedCount = deleted;
        if (failed > 0)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "partial_failure";
            outcome.Message = deleted + " type(s) deleted, " + failed + " failed. See failed[].";
        }

        if (!dryRun)
        {
            outcome.SuppressChangeSet = true;
            if (deleted > 0)
            {
                outcome.Data["undoSupported"] = false;
                outcome.Warnings.Add(
                    "Deleted family types cannot be restored by undo_last; use the Revit Undo " +
                    "command or a backup if you need them back.");
            }
        }
    }

    private static Dictionary<long, int> CountInstances(
        Document doc,
        HashSet<long> typeIds,
        CancellationToken ct)
    {
        var counts = new Dictionary<long, int>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilyInstance instance)
                continue;

            var typeId = instance.GetTypeId().GetValue();
            if (!typeIds.Contains(typeId))
                continue;

            counts[typeId] = counts.TryGetValue(typeId, out var count) ? count + 1 : 1;
        }

        return counts;
    }
}