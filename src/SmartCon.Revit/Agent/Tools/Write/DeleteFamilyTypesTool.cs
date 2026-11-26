using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Deletes family types (FamilySymbol). A type in use is refused unless
/// force=true, because Revit then also deletes its instances. The apply
/// requires confirmNames equal to a fresh recomputation of the type names
/// (S.5).
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
            "instances. The dry run returns names[] ('family : type'); apply requires " +
            "confirmNames equal to a FRESH dry-run names[] list (a model change refuses the " +
            "call). Destructive: not reversible by undo_last. Dry run by default.",
        Risk = AgentToolRisk.Destructive,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""typeIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""}},""force"":{""type"":""boolean"",""default"":false},""confirmNames"":{""type"":""array"",""items"":{""type"":""string""}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""typeIds""],""additionalProperties"":false}"
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
        var targets = new List<(long Id, string Name)>();
        foreach (var typeId in typeIds)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = typeId };
            if (doc.GetElement(ElementIdCompat.Create(typeId)) is not FamilySymbol symbol)
            {
                report.ErrorCode = "type_not_found";
                report.Reason = "Element " + typeId + " is not a family type.";
                outcome.Batch.Failed.Add(report);
                failed++;
                continue;
            }

            var name = (symbol.Family?.Name ?? "?") + " : " + symbol.Name;
            report.Name = name;
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

            targets.Add((typeId, name));
        }

        var names = targets.Select(target => target.Name).ToArray();
        AgentDeleteHelper.PublishNames(outcome, names);

        if (dryRun)
        {
            var planned = 0;
            foreach (var target in targets)
            {
                if (WorksharingScope.TryGetBlock(ElementIdCompat.Create(target.Id), out var block))
                {
                    Skip(outcome, new WriteItemReport { Id = target.Id, Name = target.Name }, block);
                    continue;
                }

                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    Reason = "Dry run: the type would be deleted."
                });
                planned++;
            }

            deleted = planned;
            outcome.AffectedCount = deleted;
            if (failed > 0)
            {
                outcome.Success = false;
                outcome.ErrorCode ??= "partial_failure";
                outcome.Message = deleted + " type(s) planned, " + failed + " failed. See failed[].";
            }

            return;
        }

        var confirm = AgentDeleteConfirm.Verify(names, AgentDeleteHelper.GetConfirmNames(args));
        if (!confirm.Ok)
        {
            AgentDeleteHelper.RefuseConfirmation(outcome, confirm);
            return;
        }

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = target.Id, Name = target.Name };
            if (WorksharingScope.TryGetBlock(ElementIdCompat.Create(target.Id), out var block))
            {
                Skip(outcome, report, block);
                continue;
            }

            if (doc.GetElement(ElementIdCompat.Create(target.Id)) is not FamilySymbol symbol)
            {
                report.ErrorCode = "type_not_found";
                report.Reason = "Type " + target.Id + " disappeared before the deletion.";
                outcome.Batch.Failed.Add(report);
                failed++;
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

        if (deleted > 0)
        {
            AgentDeleteHelper.MarkDestructiveApplied(outcome, "family types");
        }
        else
        {
            outcome.SuppressChangeSet = true;
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
