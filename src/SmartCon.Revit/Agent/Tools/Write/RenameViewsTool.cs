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
            "swaps inside the batch. Templates, sheets and system browsers are skipped. " +
            "dryRun defaults to true and only reports the plan.",
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
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var valid = new List<(RenameViewItem Item, View View)>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = item.ViewId, NewValue = item.NewName };
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

            if (usedNames.TryGetValue(newName, out var owner) && owner != item.ViewId)
            {
                Reject(outcome, report, "duplicate_name",
                    "Another view already has the name '" + newName + "' (id " + owner + ").");
                continue;
            }

            if (!planned.Add(newName))
            {
                Reject(outcome, report, "duplicate_name",
                    "The name '" + newName + "' is used by another item of this batch.");
                continue;
            }

            // The view's current name is released, so its old name becomes free.
            usedNames.Remove(view.Name);
            usedNames[newName] = item.ViewId;
            valid.Add((item, view));
        }

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

    private static void ApplyRenames(
        Document doc,
        IReadOnlyList<(RenameViewItem Item, View View)> valid,
        WriteOutcome outcome,
        bool atomic,
        CancellationToken ct)
    {
        var entries = valid
            .Select(pair => new AgentRenamePlanner.Entry
            {
                Id = pair.Item.ViewId,
                CurrentName = pair.View.Name,
                NewName = pair.Item.NewName!.Trim()
            })
            .ToList();

        var tempEntries = AgentRenamePlanner.SelectTempRenames(entries);
        if (tempEntries.Count > 0)
        {
            foreach (var entry in tempEntries)
            {
                var view = doc.GetElement(ElementIdCompat.Create(entry.Id)) as View;
                if (view is null)
                    continue;
                view.Name = AgentRenamePlanner.TempName();
            }
        }

        foreach (var pair in valid)
        {
            ct.ThrowIfCancellationRequested();

            var view = pair.View;
            var report = new WriteItemReport
            {
                Id = pair.Item.ViewId,
                Name = view.Name,
                OldValue = view.Name,
                NewValue = pair.Item.NewName!.Trim(),
                Detail = view.ViewType.ToString()
            };

            try
            {
                var oldName = view.Name;
                view.Name = pair.Item.NewName!.Trim();

                if (!string.Equals(view.Name, report.NewValue, StringComparison.Ordinal))
                {
                    report.Reason = "The view name did not change as expected.";
                    outcome.Batch.Failed.Add(report);
                    AbortIfAtomic(outcome, atomic);
                    continue;
                }

                report.OldValue = oldName;
                report.Name = view.Name;
                outcome.Batch.Changed.Add(report);
                outcome.AffectedCount++;

                outcome.Items.Add(new ChangeItem
                {
                    UniqueId = view.UniqueId,
                    ElementId = view.Id.GetValue(),
                    ParameterName = "view_name",
                    OldValue = oldName,
                    NewValue = view.Name,
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

    private sealed class RenameViewsRequest
    {
        public List<RenameViewItem>? Items { get; set; }
    }
}
