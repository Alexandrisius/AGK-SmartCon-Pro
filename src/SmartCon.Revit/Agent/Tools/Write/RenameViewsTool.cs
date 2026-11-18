using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class RenameViewsTool : AgentWriteToolBase
{
    private const int MaxItems = 200;

    public RenameViewsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "rename_views",
        Description =
            "Renames existing views by an explicit viewId → newName list. Does not create " +
            "views. Pre-checks name uniqueness across the whole project and handles name " +
            "swaps inside the batch (A→B, B→A). Templates, sheets and system browsers are " +
            "skipped. dryRun defaults to true and only reports the plan.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""newName"":{""type"":""string"",""minLength"":1}},""required"":[""viewId"",""newName""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""items""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<RenameViewsRequest>(args);
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            Fail(outcome, "no_items", "The items array is empty; pass at least one {viewId,newName} pair.");
            return;
        }

        if (items.Count > MaxItems)
        {
            Fail(outcome, "limit_exceeded",
                "At most " + MaxItems + " renames per call are allowed; split the list.");
            return;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        var usedNames = AgentViewWriteHelper.CollectViewNamesByName(doc);
        var candidates = new List<Candidate>();
        var seenIds = new HashSet<long>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = item.ViewId, NewValue = item.NewName };
            if (!seenIds.Add(item.ViewId))
            {
                Reject(outcome, report, "duplicate_item",
                    "View " + item.ViewId + " is listed more than once in this call.");
                continue;
            }

            if (!AgentViewWriteHelper.TryGetView(doc, item.ViewId, out var view, out var code, out var reason))
            {
                Reject(outcome, report, code, reason);
                continue;
            }

            if (view.IsTemplate)
            {
                Reject(outcome, report, "view_is_template",
                    "View " + item.ViewId + " is a view template; rename templates explicitly out of scope.");
                continue;
            }

            if (view.ViewType is ViewType.DrawingSheet or ViewType.ProjectBrowser
                or ViewType.SystemBrowser or ViewType.Internal)
            {
                Reject(outcome, report, "unsupported_view_type",
                    "View " + item.ViewId + " is a " + view.ViewType + " and cannot be renamed here.");
                continue;
            }

            var nameError = AgentViewWriteHelper.ValidateName(item.NewName);
            if (nameError is not null)
            {
                Reject(outcome, report, "invalid_name", nameError);
                continue;
            }

            var newName = item.NewName!.Trim();
            report.Name = view.Name;
            report.OldValue = view.Name;

            if (string.Equals(view.Name, newName, StringComparison.Ordinal))
            {
                report.Reason = "The view already has this name (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            candidates.Add(new Candidate
            {
                Id = item.ViewId,
                View = view,
                OriginalName = view.Name,
                NewName = newName
            });
        }

        var valid = SelectValid(outcome, usedNames, candidates);
        if (valid.Count == 0)
        {
            outcome.Description = "rename_views: nothing to rename";
            outcome.Message ??= "No view could be renamed; see failed[] for the reasons.";
            return;
        }

        if (atomic && outcome.Batch.Failed.Count > 0)
        {
            outcome.Aborted = true;
            outcome.Success = false;
            outcome.ErrorCode = "atomic_aborted";
            outcome.Message = outcome.Batch.Failed.Count + " item(s) failed validation and " +
                              "atomic=true, so nothing was renamed.";
            return;
        }

        outcome.Description = "Renamed " + valid.Count + " view(s)";
        ApplyRenames(doc, valid, outcome, atomic, ct);
    }

    /// <summary>
    /// Drops candidates whose target name cannot be freed by this batch. A name is
    /// available when no other element uses it, or when its owner is a valid batch
    /// member that renames away from it (the planner orders/breaks such chains).
    /// </summary>
    private static List<Candidate> SelectValid(
        WriteOutcome outcome,
        IReadOnlyDictionary<string, long> documentNames,
        IReadOnlyList<Candidate> candidates)
    {
        var candidateIds = new HashSet<long>(candidates.Select(candidate => candidate.Id));
        var rejected = new Dictionary<long, (string Code, string Reason)>();
        var valid = new List<Candidate>();
        var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (documentNames.TryGetValue(candidate.NewName, out var owner)
                && owner != candidate.Id
                && !candidateIds.Contains(owner))
            {
                rejected[candidate.Id] = (
                    "duplicate_name",
                    "Another view already has the name '" + candidate.NewName + "' (id " + owner + ").");
                continue;
            }

            if (!plannedTargets.Add(candidate.NewName))
            {
                rejected[candidate.Id] = (
                    "duplicate_name",
                    "The name '" + candidate.NewName + "' is used by another item of this batch.");
                continue;
            }

            valid.Add(candidate);
        }

        // An owner that was rejected keeps its name, so candidates depending on it must go too.
        bool removed;
        do
        {
            removed = false;
            var validIds = new HashSet<long>(valid.Select(candidate => candidate.Id));
            for (var index = valid.Count - 1; index >= 0; index--)
            {
                var candidate = valid[index];
                if (!documentNames.TryGetValue(candidate.NewName, out var owner)
                    || owner == candidate.Id
                    || validIds.Contains(owner))
                {
                    continue;
                }

                rejected[candidate.Id] = (
                    "duplicate_name",
                    "The name '" + candidate.NewName + "' stays taken: its owner (id " + owner +
                    ") is not renamed by this call.");
                valid.RemoveAt(index);
                removed = true;
            }
        }
        while (removed);

        foreach (var candidate in candidates)
        {
            if (!rejected.TryGetValue(candidate.Id, out var rejection))
                continue;

            Reject(
                outcome,
                new WriteItemReport { Id = candidate.Id, Name = candidate.OriginalName },
                rejection.Code,
                rejection.Reason);
        }

        return valid;
    }

    private static void ApplyRenames(
        Document doc,
        IReadOnlyList<Candidate> candidates,
        WriteOutcome outcome,
        bool atomic,
        CancellationToken ct)
    {
        var entries = candidates
            .Select(candidate => new AgentRenamePlanner.Entry
            {
                Id = candidate.Id,
                CurrentName = candidate.OriginalName,
                NewName = candidate.NewName
            })
            .ToList();

        var documentNames = AgentViewWriteHelper.CollectViewNamesByName(doc).Keys;
        var plan = AgentRenamePlanner.Build(entries, documentNames);
        var succeeded = new HashSet<long>();

        foreach (var entry in plan.TempFirst)
        {
            var tempView = doc.GetElement(ElementIdCompat.Create(entry.Id)) as View;
            if (tempView is null)
                continue;

            try
            {
                tempView.Name = AgentRenamePlanner.TempName();
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "Could not reserve a temporary name for view " + entry.Id + ": " + ex.Message);
            }
        }

        foreach (var entry in plan.Order)
        {
            ct.ThrowIfCancellationRequested();

            var view = doc.GetElement(ElementIdCompat.Create(entry.Id)) as View;
            var report = new WriteItemReport
            {
                Id = entry.Id,
                Name = entry.CurrentName,
                OldValue = entry.CurrentName,
                NewValue = entry.NewName
            };

            if (view is null)
            {
                report.ErrorCode = "view_not_found";
                report.Reason = "The view disappeared during the operation.";
                outcome.Batch.Failed.Add(report);
                if (AbortIfAtomic(outcome, atomic))
                    return;
                continue;
            }

            report.Detail = view.ViewType.ToString();

            try
            {
                view.Name = entry.NewName;

                if (!string.Equals(view.Name, entry.NewName, StringComparison.Ordinal))
                {
                    report.Reason = "The view name did not change as expected.";
                    outcome.Batch.Failed.Add(report);
                    if (AbortIfAtomic(outcome, atomic))
                        return;
                    continue;
                }

                report.Name = view.Name;
                outcome.Batch.Changed.Add(report);
                outcome.AffectedCount++;
                succeeded.Add(entry.Id);

                outcome.Items.Add(new ChangeItem
                {
                    UniqueId = view.UniqueId,
                    ElementId = view.Id.GetValue(),
                    ParameterName = "view_name",
                    OldValue = entry.CurrentName,
                    NewValue = entry.NewName,
                    Status = ChangeStatus.Applied
                });
            }
            catch (Exception ex)
            {
                report.ErrorCode = "rename_failed";
                report.Reason = ex.Message;
                outcome.Batch.Failed.Add(report);
                if (AbortIfAtomic(outcome, atomic))
                    return;
            }
        }

        RestoreTemporaryNames(doc, plan, succeeded, outcome);

        var planned = new HashSet<long>(plan.Order.Select(entry => entry.Id));
        foreach (var candidate in candidates.Where(candidate => !planned.Contains(candidate.Id)))
        {
            outcome.Batch.Failed.Add(new WriteItemReport
            {
                Id = candidate.Id,
                Name = candidate.OriginalName,
                NewValue = candidate.NewName,
                ErrorCode = "duplicate_name",
                Reason = "The name '" + candidate.NewName + "' is held by an element this call " +
                         "does not rename, so the rename cannot be scheduled."
            });
        }

        if (outcome.Batch.Failed.Count > 0)
            AbortIfAtomic(outcome, atomic);
    }

    private static void RestoreTemporaryNames(
        Document doc,
        AgentRenamePlanner.Plan plan,
        HashSet<long> succeeded,
        WriteOutcome outcome)
    {
        if (outcome.Aborted)
            return;

        foreach (var entry in plan.TempFirst)
        {
            if (succeeded.Contains(entry.Id))
                continue;

            if (doc.GetElement(ElementIdCompat.Create(entry.Id)) is not View view)
                continue;

            if (!view.Name.StartsWith(AgentRenamePlanner.TempPrefix, StringComparison.Ordinal))
                continue;

            try
            {
                view.Name = entry.CurrentName;
                outcome.Warnings.Add(
                    "View " + entry.Id + " kept its original name '" + entry.CurrentName +
                    "' after the failed rename.");
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "View " + entry.Id + " is left with the temporary name '" + view.Name +
                    "': " + ex.Message + " [Action: rename it manually in Revit]");
            }
        }
    }

    private static bool AbortIfAtomic(WriteOutcome outcome, bool atomic)
    {
        if (!atomic)
            return false;

        outcome.Aborted = true;
        outcome.Success = false;
        outcome.ErrorCode = "atomic_aborted";
        outcome.Message = "At least one rename failed and atomic=true, so the whole batch " +
                          "was rolled back. Nothing was changed.";
        return true;
    }

    private static void Reject(WriteOutcome outcome, WriteItemReport report, string code, string reason)
    {
        report.ErrorCode = code;
        report.Reason = reason;
        outcome.Batch.Failed.Add(report);
    }

    private static void Fail(WriteOutcome outcome, string code, string message)
    {
        outcome.Success = false;
        outcome.ErrorCode = code;
        outcome.Message = message;
    }

    private sealed class Candidate
    {
        public long Id { get; init; }

        public required View View { get; init; }

        public required string OriginalName { get; init; }

        public required string NewName { get; init; }
    }

    private sealed class RenameViewsRequest
    {
        public List<RenameViewItem>? Items { get; set; }
    }
}
