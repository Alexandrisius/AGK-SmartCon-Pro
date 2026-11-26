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
/// Deletes parameter view filters — one filter (filterId/filterName) or a batch
/// (filterIds/filterNames) in a single call. Removing a filter cannot be
/// reverted by undo_last, so every apply first saves a quarantine snapshot of
/// the filter (rules, categories, applied views with overrides) to agent.db
/// (#459) and runs as a destructive operation without a change set.
/// </summary>
public sealed class DeleteFilterTool : AgentWriteToolBase
{
    private const int MaxFilters = 100;
    private const int MaxViewsPerFilterInError = 10;

    public DeleteFilterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets,
        IAgentFilterQuarantineStore? quarantine = null)
        : base(revitContext, transactions, changeSets)
    {
        _quarantine = quarantine;
    }

    private readonly IAgentFilterQuarantineStore? _quarantine;

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "delete_filter",
        Description =
            "Deletes parameter view filters: a single filter (filterId/filterName) or a batch " +
            "(filterIds/filterNames, up to 100 in one call — 13 dead filters = one dry run + " +
            "one apply, not 26 calls). The dry run returns names[] with appliedViewsCount per " +
            "filter; apply requires confirmNames equal to a FRESH dry-run names[] list of ALL " +
            "targets (one operation = one list; a model change refuses the call) and deletes " +
            "everything in one transaction with a per-item report. deadOnly=true adds a guard: " +
            "if ANY listed filter is still applied on at least one view, the whole call fails " +
            "with filter_in_use listing the applied filters and their views — nothing is " +
            "silently skipped. Before deletion every filter is quarantined: a full snapshot " +
            "(rules, categories, applied views with overrides) is saved to agent.db and kept " +
            "90 days (quarantine.snapshotIds in the report; restoration is manual, by request). " +
            "Deletion is destructive and cannot be undone by undo_last. Dry run by default.",
        Risk = AgentToolRisk.Destructive,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""filterId"":{""type"":""integer""},""filterName"":{""type"":""string""},""filterIds"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""integer""}},""filterNames"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""string""}},""deadOnly"":{""type"":""boolean"",""default"":false},""includeAppliedViews"":{""type"":""boolean"",""default"":true},""confirmNames"":{""type"":""array"",""items"":{""type"":""string""}},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var targets = ResolveTargets(doc, args, outcome, ct);
        var requested = AgentJson.GetLongArray(args, "filterIds").Count
                        + AgentJson.GetStringArray(args, "filterNames").Count
                        + (AgentJson.GetLong(args, "filterId", 0) > 0 ? 1 : 0)
                        + (AgentJson.GetString(args, "filterName") is null ? 0 : 1);
        if (targets.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= requested == 0 ? "invalid_arguments" : outcome.ErrorCode;
            outcome.Message ??= "Pass filterId/filterName or filterIds/filterNames of existing " +
                                "view filters (see list_filters).";
            return;
        }

        var deadOnly = AgentJson.GetBool(args, "deadOnly", false);
        var includeAppliedViews = AgentJson.GetBool(args, "includeAppliedViews", true);
        // The apply path always quarantines, and the snapshot needs the applied
        // views — so the usage scan runs even when the caller skipped it.
        var needUsage = includeAppliedViews || deadOnly || !dryRun;
        var usage = needUsage ? AgentFilterUsage.Map(doc, ct) : null;

        if (deadOnly && !GuardDeadOnly(usage!, targets, outcome))
            return;

        var names = targets.Select(target => target.Name).ToArray();
        AgentDeleteHelper.PublishNames(outcome, names);

        var items = new List<object>(targets.Count);
        foreach (var target in targets)
        {
            var appliedViewsCount = usage is null
                ? (object)"notComputed"
                : AgentFilterUsage.Count(usage, target.Id);
            items.Add(new
            {
                filterId = target.Id,
                name = target.Name,
                appliedViewsCount
            });
        }

        outcome.Data["items"] = items;
        outcome.Data["undoSupported"] = false;

        if (dryRun)
        {
            foreach (var target in targets)
            {
                var count = usage is null ? (int?)null : AgentFilterUsage.Count(usage, target.Id);
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    OldValue = count.HasValue ? count.Value + " view(s)" : "notComputed",
                    Reason = "Dry run: the filter would be deleted" +
                             (count is > 0
                                 ? " and removed from " + count.Value + " view(s)."
                                 : ".")
                });
            }

            outcome.AffectedCount = targets.Count;
            outcome.Data["wouldDelete"] = true;
            outcome.Data["quarantine"] = new { enabled = _quarantine is not null, onApply = true };
            if (outcome.Batch.Failed.Count > 0)
            {
                outcome.Success = false;
                outcome.ErrorCode ??= "partial_failure";
                outcome.Message = targets.Count + " filter(s) planned, " +
                                  outcome.Batch.Failed.Count + " failed. See failed[].";
            }

            return;
        }

        var confirm = AgentDeleteConfirm.Verify(
            names, AgentDeleteHelper.GetConfirmNames(args));
        if (!confirm.Ok)
        {
            AgentDeleteHelper.RefuseConfirmation(outcome, confirm);
            return;
        }

        // Quarantine every target before the single Document.Delete call: the
        // snapshot needs the live filter (rules, categories, applied views).
        var snapshots = new List<QuarantineRow>();
        var deletableTargets = new List<AgentDeleteHelper.DeleteTarget>();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            if (doc.GetElement(ElementIdCompat.Create(target.Id))
                is not ParameterFilterElement filter)
            {
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    ErrorCode = "filter_not_found",
                    Reason = "Filter '" + target.Name + "' disappeared before the deletion."
                });
                continue;
            }

            var snapshotId = AgentFilterQuarantine.TryQuarantine(
                doc, filter, usage, Definition.Name, _quarantine, outcome.Warnings);
            snapshots.Add(new QuarantineRow(
                target.Id, target.Name, snapshotId, snapshotId is not null));
            deletableTargets.Add(new AgentDeleteHelper.DeleteTarget
            {
                Id = target.Id,
                Name = target.Name
            });
        }

        var deletedCount = AgentDeleteHelper.DeleteBatch(
            doc, deletableTargets, outcome, "filter(s)", ct, WorksharingScope);
        outcome.AffectedCount = deletedCount;

        outcome.Data["quarantine"] = new
        {
            enabled = _quarantine is not null,
            retentionDays = 90,
            snapshots,
            restore = "manual, by request: the snapshot holds rules, categories and applied " +
                      "views with overrides"
        };
        AnnotateQuarantineInReports(outcome, snapshots);

        if (outcome.Batch.Failed.Count > 0)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "partial_failure";
            outcome.Message = deletedCount + " filter(s) deleted, " +
                              outcome.Batch.Failed.Count + " failed. See failed[].";
        }

        if (deletedCount > 0)
        {
            outcome.SuppressChangeSet = true;
            outcome.Data["undoSupported"] = false;
            outcome.Warnings.Add(
                "Deleted filter(s) cannot be restored by undo_last; a quarantine snapshot of " +
                "each filter (rules, categories, applied views with overrides) was saved to " +
                "agent.db and is kept 90 days — restoration is manual, by request. Use the " +
                "Revit Undo command immediately after the deletion if the filters must come " +
                "back right now.");
        }
        else
        {
            outcome.SuppressChangeSet = true;
        }
    }

    /// <summary>
    /// Resolves the single arguments plus the batch arrays into deduplicated
    /// delete targets. Unknown ids/names land in failed[] per item instead of
    /// failing the whole call (the valid part of the request stays meaningful).
    /// </summary>
    private static List<AgentDeleteHelper.DeleteTarget> ResolveTargets(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        CancellationToken ct)
    {
        var targets = new List<AgentDeleteHelper.DeleteTarget>();
        var seen = new HashSet<long>();

        var singleId = AgentJson.GetLong(args, "filterId", 0);
        if (singleId > 0)
            Accept(doc, singleId, null, outcome, targets, seen, ct);

        var singleName = AgentJson.GetString(args, "filterName");
        if (!string.IsNullOrWhiteSpace(singleName))
            Accept(doc, null, singleName, outcome, targets, seen, ct);

        foreach (var filterId in AgentJson.GetLongArray(args, "filterIds"))
        {
            ct.ThrowIfCancellationRequested();
            Accept(doc, filterId, null, outcome, targets, seen, ct);
        }

        foreach (var filterName in AgentJson.GetStringArray(args, "filterNames"))
        {
            ct.ThrowIfCancellationRequested();
            Accept(doc, null, filterName, outcome, targets, seen, ct);
        }

        if (targets.Count + outcome.Batch.Failed.Count > MaxFilters)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "At most " + MaxFilters +
                              " filters can be deleted per call; split the request.";
            targets.Clear();
        }

        return targets;
    }

    private static void Accept(
        Document doc,
        long? filterId,
        string? filterName,
        WriteOutcome outcome,
        List<AgentDeleteHelper.DeleteTarget> targets,
        HashSet<long> seen,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var report = new WriteItemReport
        {
            Id = filterId,
            Name = filterName
        };

        ParameterFilterElement? filter = null;
        if (filterId is > 0)
            filter = AgentFilterWriteHelper.FindById(doc, filterId.Value);
        if (filter is null && !string.IsNullOrWhiteSpace(filterName))
            filter = AgentFilterWriteHelper.FindByName(doc, filterName);

        if (filter is null)
        {
            report.ErrorCode = "filter_not_found";
            report.Reason = "No view filter " +
                            (filterId is > 0
                                ? "with id " + filterId
                                : "named '" + filterName + "'") +
                            ". Use list_filters to see the existing filters.";
            outcome.Batch.Failed.Add(report);
            return;
        }

        // Cache Name before Delete: reading it after the element is gone throws
        // InvalidObjectException (stress-test regression A).
        var resolvedName = filter.Name;
        var resolvedId = filter.Id.GetValue();
        if (!seen.Add(resolvedId))
            return;

        targets.Add(new AgentDeleteHelper.DeleteTarget
        {
            Id = resolvedId,
            Name = resolvedName
        });
    }

    /// <summary>
    /// deadOnly guard: a listed filter that is still applied on at least one
    /// view fails the WHOLE call with the applied list — the tool never
    /// silently narrows the request (the model would believe a filter was
    /// deleted when it was actually kept).
    /// </summary>
    private static bool GuardDeadOnly(
        Dictionary<long, List<View>> usage,
        List<AgentDeleteHelper.DeleteTarget> targets,
        WriteOutcome outcome)
    {
        var applied = new List<object>();
        foreach (var target in targets)
        {
            if (!usage.TryGetValue(target.Id, out var views) || views.Count == 0)
                continue;

            var viewRows = new List<object>();
            foreach (var view in views.Take(MaxViewsPerFilterInError))
            {
                viewRows.Add(new { viewId = view.Id.GetValue(), viewName = view.Name });
            }

            applied.Add(new
            {
                filterId = target.Id,
                name = target.Name,
                appliedViewCount = views.Count,
                views = viewRows
            });
        }

        if (applied.Count == 0)
            return true;

        outcome.Success = false;
        outcome.ErrorCode = "filter_in_use";
        outcome.Message = "deadOnly=true: " + applied.Count + " of the listed filters are " +
                          "still applied to views, so NOTHING was deleted. Remove them from " +
                          "the list, or call again with deadOnly=false to delete them anyway " +
                          "(Revit will remove them from those views). See appliedFilters[].";
        outcome.Data["appliedFilters"] = applied;
        outcome.Data["undoSupported"] = false;
        return false;
    }

    private static void AnnotateQuarantineInReports(
        WriteOutcome outcome,
        List<QuarantineRow> snapshots)
    {
        if (snapshots.Count == 0)
            return;

        var byFilterId = snapshots.ToDictionary(row => row.FilterId);
        foreach (var report in outcome.Batch.Changed)
        {
            if (report.Id is not { } reportId || !byFilterId.TryGetValue(reportId, out var row))
                continue;

            report.Detail = (string.IsNullOrWhiteSpace(report.Detail)
                    ? string.Empty
                    : report.Detail + "; ")
                + "quarantined=" + row.Quarantined.ToString().ToLowerInvariant()
                + (row.SnapshotId is null ? string.Empty : ", snapshotId=" + row.SnapshotId);
        }
    }

    private sealed record QuarantineRow(
        long FilterId,
        string Name,
        string? SnapshotId,
        bool Quarantined);
}
