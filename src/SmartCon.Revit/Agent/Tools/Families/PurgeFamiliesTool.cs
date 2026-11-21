using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Deletes loadable families that have no placed instances. The dry run lists
/// the candidate families (name, symbol count, in-place flag); the apply
/// requires confirmNames equal to a fresh recomputation of that list (S.5).
/// Not reversible by undo_last.
/// </summary>
public sealed class PurgeFamiliesTool : AgentWriteToolBase
{
    public PurgeFamiliesTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "purge_families",
        Description =
            "Deletes unused loadable families: a family is unused when none of its types has a " +
            "placed instance. Without familyNames every unused family is a candidate; with " +
            "familyNames only those families (an unknown name fails per-item). The dry run " +
            "returns candidates[] (familyName, symbolCount, isInPlace) and names[]; the apply " +
            "requires confirmNames equal to a FRESH dry-run names[] list (a model change " +
            "refuses the call). In-place families are included but flagged isInPlace=true — " +
            "they may still have an intended purpose, review them with the user. Destructive: " +
            "not reversible by undo_last. Dry run by default.",
        Risk = AgentToolRisk.Destructive,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyNames"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Restrict the purge to these families; omit to scan all""},""confirmNames"":{""type"":""array"",""items"":{""type"":""string""}},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var requestedNames = AgentJson.GetStringArray(args, "familyNames");
        var requestedSet = new HashSet<string>(requestedNames, StringComparer.OrdinalIgnoreCase);

        var candidates = CollectCandidates(doc, requestedSet, ct, out var skippedInUse);
        if (requestedSet.Count > 0)
        {
            foreach (var requested in requestedNames)
            {
                ct.ThrowIfCancellationRequested();

                var exists = false;
                foreach (var family in AgentFamilyReader.LoadableFamilies(doc))
                {
                    if (string.Equals(family.Name, requested, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    outcome.Batch.Failed.Add(new WriteItemReport
                    {
                        Name = requested,
                        ErrorCode = "family_not_found",
                        Reason = "No loaded family named '" + requested + "'."
                    });
                }
            }
        }

        var names = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
            names.Add(candidate.Name);

        outcome.Description = "Purge unused families (" + names.Count + ")";
        outcome.Data["candidates"] = candidates;
        AgentDeleteHelper.PublishNames(outcome, names);

        if (dryRun)
        {
            foreach (var skipped in skippedInUse)
                outcome.Batch.Skipped.Add(skipped);

            foreach (var candidate in candidates)
            {
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = candidate.Id,
                    Name = candidate.Name,
                    Detail = candidate.SymbolCount + " type(s)" +
                             (candidate.IsInPlace ? ", in-place" : string.Empty),
                    Reason = "Dry run: the family would be deleted with all its types."
                });
            }

            outcome.AffectedCount = candidates.Count;
            if (outcome.Batch.Failed.Count > 0)
            {
                outcome.Success = false;
                outcome.ErrorCode = "partial_failure";
                outcome.Message = candidates.Count + " family(ies) planned, " +
                                  outcome.Batch.Failed.Count + " failed. See failed[].";
            }

            return;
        }

        var confirm = AgentDeleteConfirm.Verify(names, AgentDeleteHelper.GetConfirmNames(args));
        if (!confirm.Ok)
        {
            AgentDeleteHelper.RefuseConfirmation(outcome, confirm);
            return;
        }

        var targets = candidates
            .Select(candidate => new AgentDeleteHelper.DeleteTarget
            {
                Id = candidate.Id,
                Name = candidate.Name
            })
            .ToList();
        var deleted = AgentDeleteHelper.DeleteBatch(doc, targets, outcome, "families", ct);
        outcome.Data["deletedFamilies"] = deleted;
        outcome.Data["symbolsDeleted"] = outcome.Data.TryGetValue(
            "dependentsDeleted",
            out var dependents)
            ? dependents
            : 0;

        if (deleted > 0)
        {
            AgentDeleteHelper.MarkDestructiveApplied(outcome, "families");
        }
        else
        {
            outcome.SuppressChangeSet = true;
        }
    }

    private static List<FamilyCandidate> CollectCandidates(
        Document doc,
        HashSet<string> requestedNames,
        CancellationToken ct,
        out List<WriteItemReport> skippedInUse)
    {
        var usedFamilyIds = new HashSet<long>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is FamilyInstance instance && instance.Symbol is not null)
                usedFamilyIds.Add(instance.Symbol.Family.Id.GetValue());
        }

        skippedInUse = new List<WriteItemReport>();
        var candidates = new List<FamilyCandidate>();
        foreach (var family in AgentFamilyReader.LoadableFamilies(doc))
        {
            ct.ThrowIfCancellationRequested();

            var familyId = family.Id.GetValue();
            if (usedFamilyIds.Contains(familyId))
            {
                if (requestedNames.Count > 0 && requestedNames.Contains(family.Name))
                {
                    skippedInUse.Add(new WriteItemReport
                    {
                        Id = familyId,
                        Name = family.Name,
                        ErrorCode = "family_in_use",
                        Reason = "The family has placed instances, so it is not a purge " +
                                 "candidate and will not be deleted."
                    });
                }

                continue;
            }

            if (requestedNames.Count > 0 && !requestedNames.Contains(family.Name))
                continue;

            candidates.Add(new FamilyCandidate
            {
                Id = familyId,
                Name = family.Name,
                SymbolCount = family.GetFamilySymbolIds().Count,
                IsInPlace = family.IsInPlace
            });
        }

        return candidates;
    }

    private sealed class FamilyCandidate
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public int SymbolCount { get; init; }

        public bool IsInPlace { get; init; }
    }
}
