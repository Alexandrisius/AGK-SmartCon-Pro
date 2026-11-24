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
/// Merges a duplicate view filter into its canonical twin (S.27, #460): every
/// view that applies the duplicate is switched to the canonical filter
/// (AddFilter + RemoveFilter), then the duplicate is deleted with a quarantine
/// snapshot (#459). Atomic: one transaction, all views or nothing.
/// </summary>
public sealed class MergeDuplicateFiltersTool : AgentWriteToolBase
{
    public MergeDuplicateFiltersTool(
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
        Name = "merge_duplicate_filters",
        Description =
            "Merges ONE duplicate view filter into its canonical twin: for every view that " +
            "applies the duplicate, the canonical filter is added and the duplicate removed " +
            "(views that already apply the canonical only lose the duplicate); the duplicate's " +
            "per-view visibility flag (V/G «Видимость») is carried over to the canonical filter " +
            "(unreadable ones are listed in filterVisibilityNotTransferred); then the " +
            "duplicate filter is deleted. list_filters.duplicateGroups shows the groups and " +
            "which copy is alive (appliedViewsCount). The dry run returns the per-view plan " +
            "(view x operation) and names[]; apply requires confirmNames equal to the FRESH " +
            "names[] list (the duplicate name). Graphic overrides of the duplicate are NOT " +
            "transferred — views with non-default overrides are listed in " +
            "overridesNotTransferred. Everything happens in one transaction: if any view " +
            "refuses, nothing changes. Before the deletion the duplicate is quarantined " +
            "(full snapshot to agent.db, kept 90 days, quarantine.snapshotId). The merge " +
            "cannot be undone by undo_last (the deleted duplicate cannot be recreated " +
            "automatically) — the quarantine snapshot holds everything for a manual restore. " +
            "Dry run by default.",
        Risk = AgentToolRisk.Destructive,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""duplicateFilterId"":{""type"":""integer""},""duplicateFilterName"":{""type"":""string""},""canonicalFilterId"":{""type"":""integer""},""canonicalFilterName"":{""type"":""string""},""confirmNames"":{""type"":""array"",""items"":{""type"":""string""}},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var duplicate = Resolve(
            doc,
            AgentJson.GetLong(args, "duplicateFilterId", 0),
            AgentJson.GetString(args, "duplicateFilterName"));
        var canonical = Resolve(
            doc,
            AgentJson.GetLong(args, "canonicalFilterId", 0),
            AgentJson.GetString(args, "canonicalFilterName"));

        if (duplicate is null || canonical is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "filter_not_found";
            outcome.Message = "Pass duplicateFilterId/Name and canonicalFilterId/Name of two " +
                              "existing view filters (see list_filters duplicateGroups).";
            return;
        }

        // Cache names before any mutation: reading them after Delete throws.
        var duplicateName = duplicate.Name;
        var canonicalName = canonical.Name;
        var duplicateId = duplicate.Id;
        var canonicalId = canonical.Id;
        if (duplicateId == canonicalId)
        {
            outcome.Success = false;
            outcome.ErrorCode = "same_filter";
            outcome.Message = "The duplicate and the canonical filter are the same element (" +
                              duplicateName + "); nothing to merge.";
            return;
        }

        ct.ThrowIfCancellationRequested();
        var usage = AgentFilterUsage.Map(doc, ct);
        usage.TryGetValue(duplicateId.GetValue(), out var duplicateViews);
        duplicateViews ??= new List<View>();
        var canonicalViewIds = new HashSet<long>(
            usage.TryGetValue(canonicalId.GetValue(), out var canonicalViews)
                ? canonicalViews.Select(view => view.Id.GetValue())
                : Array.Empty<long>());

        // Plan: view x operation. Views with custom overrides on the duplicate
        // keep nothing — AddFilter starts from default overrides. The per-view
        // visibility flag ("Видимость" in V/G) is transferred: AddFilter starts
        // visible, which would un-hide everything the duplicate hid (S.42).
        var plan = new List<object>();
        var overridesNotTransferred = new List<object>();
        var visibilityNotTransferred = new List<object>();
        var savedVisibility = new Dictionary<long, bool>();
        foreach (var view in duplicateViews)
        {
            var canonicalApplied = canonicalViewIds.Contains(view.Id.GetValue());
            plan.Add(new
            {
                viewId = view.Id.GetValue(),
                viewName = view.Name,
                action = canonicalApplied ? "removeOnly" : "replace",
                canonicalAlreadyApplied = canonicalApplied
            });

            if (HasNonDefaultOverrides(view, duplicateId))
            {
                overridesNotTransferred.Add(new
                {
                    viewId = view.Id.GetValue(),
                    viewName = view.Name
                });
            }

            if (canonicalApplied)
                continue;

            var visible = TryReadFilterVisibility(view, duplicateId);
            if (visible.HasValue)
                savedVisibility[view.Id.GetValue()] = visible.Value;
            else
                visibilityNotTransferred.Add(new
                {
                    viewId = view.Id.GetValue(),
                    viewName = view.Name
                });
        }

        AgentDeleteHelper.PublishNames(outcome, new[] { duplicateName });
        outcome.Data["duplicateFilter"] = new
        {
            filterId = duplicateId.GetValue(),
            name = duplicateName,
            appliedViewCount = duplicateViews.Count
        };
        outcome.Data["canonicalFilter"] = new
        {
            filterId = canonicalId.GetValue(),
            name = canonicalName,
            appliedViewCountBefore = canonicalViewIds.Count
        };
        outcome.Data["plan"] = plan;
        outcome.Data["viewsAffected"] = duplicateViews.Count;
        outcome.Data["overridesNotTransferred"] = overridesNotTransferred;
        outcome.Data["filterVisibilityNotTransferred"] = visibilityNotTransferred;
        outcome.Data["undoSupported"] = false;

        if (overridesNotTransferred.Count > 0)
        {
            outcome.Warnings.Add(
                overridesNotTransferred.Count + " view(s) had graphic overrides on the " +
                "duplicate; overrides are NOT transferred to the canonical filter — reapply " +
                "them with set_filter_overrides if needed (see overridesNotTransferred).");
        }

        if (visibilityNotTransferred.Count > 0)
        {
            outcome.Warnings.Add(
                visibilityNotTransferred.Count + " view(s): the duplicate's visibility flag " +
                "(V/G «Видимость») could not be read, so the canonical filter starts visible — " +
                "check V/G on these views after the merge (see filterVisibilityNotTransferred). " +
                "[Action: review the listed views and re-hide the filter with set_filter_overrides " +
                "or the V/G dialog if needed]");
        }

        if (dryRun)
        {
            foreach (var view in duplicateViews)
            {
                var canonicalApplied = canonicalViewIds.Contains(view.Id.GetValue());
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = view.Id.GetValue(),
                    Name = view.Name,
                    OldValue = duplicateName,
                    NewValue = canonicalApplied ? "(duplicate removed)" : canonicalName,
                    Reason = "Dry run: " + (canonicalApplied
                        ? "the duplicate would be removed, the canonical filter is already " +
                          "applied here."
                        : "the duplicate would be replaced by the canonical filter.")
                });
            }

            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Id = duplicateId.GetValue(),
                Name = duplicateName,
                Reason = "Dry run: the duplicate filter would be deleted (quarantined first)."
            });
            outcome.AffectedCount = duplicateViews.Count + 1;
            return;
        }

        var confirm = AgentDeleteConfirm.Verify(
            new[] { duplicateName }, AgentDeleteHelper.GetConfirmNames(args));
        if (!confirm.Ok)
        {
            AgentDeleteHelper.RefuseConfirmation(outcome, confirm);
            return;
        }

        // Quarantine before the first mutation: the snapshot needs the live
        // duplicate with its rules, categories and applied views.
        var snapshotId = AgentFilterQuarantine.TryQuarantine(
            doc, duplicate, usage, Definition.Name, _quarantine, outcome.Warnings);

        // One transaction, atomic: add the canonical filter first (while the
        // duplicate is still applied — a failure leaves the view untouched),
        // then remove the duplicate; any refusal aborts the whole merge.
        foreach (var view in duplicateViews)
        {
            ct.ThrowIfCancellationRequested();
            var applied = AgentFilterUsage.FiltersOf(view);
            try
            {
                var canonicalWasApplied = applied.Contains(canonicalId.GetValue());
                if (!canonicalWasApplied)
                {
                    view.AddFilter(canonicalId);
                    if (savedVisibility.TryGetValue(view.Id.GetValue(), out var visible))
                        view.SetFilterVisibility(canonicalId, visible);
                }

                if (applied.Contains(duplicateId.GetValue()))
                    view.RemoveFilter(duplicateId);

                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = view.Id.GetValue(),
                    Name = view.Name,
                    OldValue = duplicateName,
                    NewValue = canonicalName,
                    Detail = canonicalViewIds.Contains(view.Id.GetValue())
                        ? "duplicate removed; canonical was already applied"
                        : "duplicate replaced by the canonical filter"
                });
            }
            catch (Exception ex)
            {
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Id = view.Id.GetValue(),
                    Name = view.Name,
                    ErrorCode = "view_refused",
                    Reason = "Revit refused to switch filter '" + duplicateName +
                             "' to '" + canonicalName + "' on view '" + view.Name + "': " +
                             ex.Message
                });
                outcome.Success = false;
                outcome.ErrorCode = "merge_aborted";
                outcome.Message = "View '" + view.Name + "' refused the merge; the whole " +
                                  "operation was rolled back, nothing changed.";
                outcome.Aborted = true;
                return;
            }
        }

        outcome.Data["quarantine"] = new
        {
            enabled = _quarantine is not null,
            retentionDays = 90,
            snapshotId,
            quarantined = snapshotId is not null,
            restore = "manual, by request: the snapshot holds rules, categories and applied " +
                      "views with overrides"
        };

        try
        {
            var deletedIds = doc.Delete(duplicateId);
            var deleted = false;
            foreach (var deletedId in deletedIds)
            {
                if (deletedId == duplicateId)
                    deleted = true;
            }

            if (!deleted)
            {
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Id = duplicateId.GetValue(),
                    Name = duplicateName,
                    ErrorCode = "filter_not_deleted",
                    Reason = "Revit did not report the duplicate as deleted; the merge was " +
                             "rolled back."
                });
                outcome.Success = false;
                outcome.ErrorCode = "merge_aborted";
                outcome.Message = "Revit refused to delete the duplicate filter '" +
                                  duplicateName + "'; the whole operation was rolled back.";
                outcome.Aborted = true;
                return;
            }
        }
        catch (Exception ex)
        {
            outcome.Batch.Failed.Add(new WriteItemReport
            {
                Id = duplicateId.GetValue(),
                Name = duplicateName,
                ErrorCode = "delete_failed",
                Reason = "Revit refused to delete filter '" + duplicateName + "': " + ex.Message
            });
            outcome.Success = false;
            outcome.ErrorCode = "merge_aborted";
            outcome.Message = "Deleting the duplicate filter failed; the whole operation was " +
                              "rolled back.";
            outcome.Aborted = true;
            return;
        }

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = duplicateId.GetValue(),
            Name = duplicateName,
            NewValue = "deleted",
            Detail = "duplicate merged into '" + canonicalName + "'" +
                     (snapshotId is null
                         ? string.Empty
                         : "; quarantined=true, snapshotId=" + snapshotId)
        });
        outcome.AffectedCount = duplicateViews.Count + 1;
        outcome.SuppressChangeSet = true;
        outcome.Data["undoSupported"] = false;
        outcome.Warnings.Add(
            "The merge cannot be undone by undo_last: the duplicate is deleted, and a view " +
            "cannot re-apply a filter that no longer exists. The quarantine snapshot " +
            "(agent.db, kept 90 days) holds the rules, categories and per-view overrides " +
            "for a manual restore; use the Revit Undo command immediately if the merge " +
            "must come back.");
    }

    private static ParameterFilterElement? Resolve(
        Document doc,
        long filterId,
        string? filterName)
    {
        var filter = filterId > 0
            ? AgentFilterWriteHelper.FindById(doc, filterId)
            : null;
        return filter ?? AgentFilterWriteHelper.FindByName(doc, filterName);
    }

    private static bool HasNonDefaultOverrides(View view, ElementId filterId)
    {
        try
        {
            return !AgentFilterWriteHelper
                .Describe(view.GetFilterOverrides(filterId))
                .IsEmpty;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool? TryReadFilterVisibility(View view, ElementId filterId)
    {
        try
        {
            return view.GetFilterVisibility(filterId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
