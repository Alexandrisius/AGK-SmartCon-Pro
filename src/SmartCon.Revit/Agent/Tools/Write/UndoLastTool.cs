using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Reverts the last applied change set of the active document (mvp-plan §9.4).
/// Only changes recorded by SmartCon are touched; items whose current value no
/// longer matches the recorded new value are reported as conflicts and skipped.
/// </summary>
public sealed partial class UndoLastTool : AgentWriteToolBase
{
    private readonly IJobStore? _jobs;

    public UndoLastTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets,
        IJobStore? jobs = null)
        : base(revitContext, transactions, changeSets)
    {
        _jobs = jobs;
    }

    /// <summary>
    /// Test/undo_until seam: runs the revert of one change set inside the
    /// CALLER-managed transaction (undo_until dry run simulates the whole chain
    /// in a single rolling-back transaction, so each older set sees the state
    /// the newer reverts would produce).
    /// </summary>
    internal void ApplyForTests(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct,
        AgentWorksharingScope? worksharing = null)
    {
        if (worksharing is not null)
            UseWorksharingScope(worksharing);

        Apply(doc, args, outcome, dryRun, ct);
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "undo_last",
        Description =
            "Reverts the last change set that SmartCon applied to the active document by a write " +
            "tool. Changes applied by start_job are not change sets — when the newest write is a " +
            "job, the answer names the exact rollback_job(jobId) call to make. " +
            "Items changed by somebody else after the operation are reported as conflicts " +
            "and skipped (pass force=true to restore them anyway). Category visibility is " +
            "compared on the categories this view can toggle, so a later edit of its template is " +
            "not a conflict. dryRun defaults to true " +
            "and shows what would be restored.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""changeSetId"":{""type"":""string""},""force"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var documentId = AgentDocumentIdentity.GetDocumentId(doc);
        var force = AgentJson.GetBool(args, "force", false);

        ChangeSetDetails? details;
        var requestedId = AgentJson.GetString(args, "changeSetId");
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            if (!Guid.TryParse(requestedId, out var parsedId))
            {
                outcome.Success = false;
                outcome.ErrorCode = "invalid_change_set_id";
                outcome.Message = "'" + requestedId + "' is not a valid change set id (GUID).";
                return;
            }

            details = AsyncBridge.RunSync(() => ChangeSets.GetAsync(parsedId, ct));
            if (details is null)
            {
                outcome.Success = false;
                outcome.ErrorCode = "change_set_not_found";
                outcome.Message = "Change set " + requestedId + " was not found.";
                return;
            }

            if (!details.Header.AppliedAt.HasValue)
            {
                outcome.Success = false;
                outcome.ErrorCode = "change_set_not_applied";
                outcome.Message = "Change set " + requestedId +
                                  " is not applied (already rolled back or never applied).";
                return;
            }
        }
        else
        {
            details = AsyncBridge.RunSync(() => ChangeSets.GetLastAppliedAsync(documentId, ct));
            if (details is null)
            {
                // The last write may be a JOB, which is not a change set: one
                // honest redirect with the concrete command beats a dead-end
                // "nothing to undo" (S.47, #532).
                var jobRedirect = LastAppliedJobRedirect(documentId, ct);
                if (jobRedirect is not null)
                {
                    outcome.Success = false;
                    outcome.ErrorCode = "job_needs_rollback_job";
                    outcome.Message = jobRedirect;
                    return;
                }

                outcome.Success = false;
                outcome.ErrorCode = "nothing_to_undo";
                outcome.Message = "There is no applied change set for the active document.";
                return;
            }

            // A job applied AFTER the newest change set is the newer operation:
            // reverting the older set silently would skip it. Redirect unless the
            // caller named the change set explicitly.
            var newerJob = LastAppliedJobRedirect(documentId, ct, after: details.Header.AppliedAt);
            if (newerJob is not null)
            {
                outcome.Success = false;
                outcome.ErrorCode = "job_needs_rollback_job";
                outcome.Message = newerJob;
                return;
            }
        }

        if (!string.Equals(details.Header.DocumentId, documentId, StringComparison.Ordinal))
        {
            outcome.Success = false;
            outcome.ErrorCode = "document_mismatch";
            outcome.Message = "Change set " + details.Header.Id.ToString("N") +
                              " belongs to a different document.";
            return;
        }

        outcome.Description = "Undo of change set " + details.Header.Id.ToString("N") +
                              " (" + details.Header.Operation + ")";
        outcome.SuppressChangeSet = true;
        outcome.RollBackChangeSetId = details.Header.Id;

        var renames = new List<(ChangeItem Item, Element Element)>();
        var sheetRenames = new List<(ChangeItem Item, ViewSheet Sheet)>();
        var elementTypes = new List<(ChangeItem Item, Element? Element)>();

        for (var index = details.Items.Count - 1; index >= 0; index--)
        {
            ct.ThrowIfCancellationRequested();

            var item = details.Items[index];
            var key = item.ParameterName ?? string.Empty;

            // An element owned by another user or out of date rolls back the whole
            // undo transaction: skip that item and keep the rest.
            var ownershipTarget = ResolveElement(doc, item);
            if (ownershipTarget is not null
                && WorksharingScope.TryGetBlock(ownershipTarget.Id, out var ownershipBlock))
            {
                Skip(outcome, CreateReport(item), ownershipBlock);
                continue;
            }

            // Family edits are reverted after the project transaction: restoring a
            // family requires EditFamily/LoadFamily, which cannot run while the
            // project document is modifiable (UndoLastTool.Families.cs).
            if (key == "family")
            {
                RegisterFamilyRestore(outcome, item);
                continue;
            }

            // G.4 family type operations are reverted by UndoLastTool.FamilyTypes.cs:
            // a duplicated type is deleted, a renamed type gets its old name back,
            // a loaded family is deleted and an overwrite reload is refused.
            if (key is "family_type_created" or "family_type_name"
                or "family_loaded" or "family_overwritten")
            {
                var report = CreateReport(item);
                var element = ResolveElement(doc, item);
                var (reverted, revertError, revertReason) =
                    RevertFamilyElementSafe(doc, element, item, key, force);
                if (reverted)
                {
                    outcome.Batch.Changed.Add(report);
                    outcome.AffectedCount++;
                }
                else if (revertError is null)
                {
                    report.Reason = revertReason;
                    outcome.Batch.Skipped.Add(report);
                }
                else
                {
                    report.ErrorCode = revertError;
                    report.Reason = revertReason;
                    outcome.Batch.Failed.Add(report);
                }

                continue;
            }

            // replace_family_type items are reverted as one batch after the loop:
            // elements are grouped by their previous type so each group is a single
            // static ChangeTypeId call (UndoLastTool.FamilyTypes.cs).
            if (key == "element_type")
            {
                elementTypes.Add((item, ResolveElement(doc, item)));
                continue;
            }

            // G.6 view tools: a created view is deleted, a browser folder restored.
            if (key is "view_created" or "browser_folder")
            {
                RecordViewRevert(doc, outcome, item, key, force);
                continue;
            }

            // View-filter items are reverted by UndoLastTool.Filters.cs: the recorded
            // filter state is restored, a filter created by the set is deleted.
            if (key is "filter" or "filter_created")
            {
                RecordFilterRevert(doc, outcome, item, key, force);
                continue;
            }

            if (key is "view_name" or "level_name")
            {
                var element = ResolveElement(doc, item);
                var report = CreateReport(item);
                if (element is null)
                {
                    report.ErrorCode = "element_not_found";
                    report.Reason = "The element no longer exists in the document.";
                    outcome.Batch.Failed.Add(report);
                    continue;
                }

                if ((key == "view_name" && element is not View)
                    || (key == "level_name" && element is not Level))
                {
                    report.ErrorCode = "unsupported_element";
                    report.Reason = "Element " + item.ElementId + " does not match '" + key + "'.";
                    outcome.Batch.Failed.Add(report);
                    continue;
                }

                renames.Add((item, element));
                continue;
            }

            // Schedule changes are reverted by UndoLastTool.Schedules.cs: created
            // schedules and placements are deleted, removed placements are
            // re-created, placements are moved back or rotated back, renames are
            // reverted and the fields/format/style/totals/filters/sorting
            // snapshots are restored.
            if (key is "schedule_created" or "schedule_placed" or "schedule_fields"
                or "schedule_filters" or "schedule_sort" or "schedule_moved"
                or "schedule_totals" or "schedule_renamed" or "schedule_unplaced"
                or "schedule_field_format" or "schedule_style" or "schedule_rotated")
            {
                var report = CreateReport(item);
                var element = ResolveElement(doc, item);
                var (reverted, revertError, revertReason) =
                    RevertScheduleChange(doc, element, item, key, force);
                if (reverted)
                {
                    outcome.Batch.Changed.Add(report);
                    outcome.AffectedCount++;
                }
                else if (revertError is null)
                {
                    report.Reason = revertReason;
                    outcome.Batch.Skipped.Add(report);
                }
                else
                {
                    report.ErrorCode = revertError;
                    report.Reason = revertReason;
                    outcome.Batch.Failed.Add(report);
                }

                continue;
            }

            // MEP system type property changes (name/abbreviation) are reverted by
            // UndoLastTool.Mep.cs from the recorded value snapshot.
            if (key == "mep_system_type")
            {
                RecordMepSystemTypeRevert(doc, outcome, item, force);
                continue;
            }

            // Project parameter bindings are reverted by UndoLastTool.Parameters.cs:
            // a created binding is removed, an extended one is re-bound to the
            // recorded category set.
            if (key == "project_parameter_binding")
            {
                var report = CreateReport(item);
                var (reverted, revertError, revertReason) =
                    RevertProjectParameterBinding(doc, item, force);
                if (reverted)
                {
                    outcome.Batch.Changed.Add(report);
                    outcome.AffectedCount++;
                }
                else if (revertError is null)
                {
                    report.Reason = revertReason;
                    outcome.Batch.Skipped.Add(report);
                }
                else
                {
                    report.ErrorCode = revertError;
                    report.Reason = revertReason;
                    outcome.Batch.Failed.Add(report);
                }

                continue;
            }

            // Sheet changes are reverted by UndoLastTool.Sheets.cs: created
            // elements are deleted, centers restored, removed placements
            // re-created, and number/name values planned as one batch (a swap
            // needs temporary values).
            if (key is "sheet_created" or "viewport_created" or "viewport_center"
                or "viewport_removed")
            {
                RevertSheetElementItem(doc, item, key, force, outcome);
                continue;
            }

            if (key is "sheet_number" or "sheet_name")
            {
                var element = ResolveElement(doc, item);
                var report = CreateReport(item);
                if (element is null)
                {
                    report.ErrorCode = "element_not_found";
                    report.Reason = "The sheet no longer exists in the document.";
                    outcome.Batch.Failed.Add(report);
                    continue;
                }

                if (element is not ViewSheet sheet)
                {
                    report.ErrorCode = "unsupported_element";
                    report.Reason = "Element " + item.ElementId + " is not a sheet.";
                    outcome.Batch.Failed.Add(report);
                    continue;
                }

                sheetRenames.Add((item, sheet));
                continue;
            }

            var genericReport = CreateReport(item);

            var genericElement = ResolveElement(doc, item);
            if (genericElement is null)
            {
                genericReport.ErrorCode = "element_not_found";
                genericReport.Reason = "The element no longer exists in the document.";
                outcome.Batch.Failed.Add(genericReport);
                continue;
            }

            var (ok, errorCode, reason) = RevertSafely(doc, genericElement, item, force);
            if (ok)
            {
                outcome.Batch.Changed.Add(genericReport);
                outcome.AffectedCount++;
            }
            else if (errorCode is null)
            {
                genericReport.Reason = reason;
                outcome.Batch.Skipped.Add(genericReport);
            }
            else
            {
                genericReport.ErrorCode = errorCode;
                genericReport.Reason = reason;
                outcome.Batch.Failed.Add(genericReport);
            }
        }

        RevertRenames(doc, renames, outcome, force);
        RevertSheetRenames(doc, sheetRenames, outcome, force);
        RevertElementTypes(doc, elementTypes, outcome, force);

        // Family restores run after the project transaction and decide the final
        // success/affected state themselves (UndoLastTool.Families.cs).
        if (HasPendingFamilyRestores(outcome))
            return;

        // Only real failures keep the set applied. Skipped items are "already restored"
        // or unrestorable — they must not block closing the set forever.
        var notReverted = outcome.Batch.Failed.Count;

        if (outcome.AffectedCount == 0)
        {
            if (notReverted == 0)
            {
                // Everything that was not reverted is blocked by worksharing: the set
                // must stay applied and the caller gets the actionable code, not
                // "nothing to undo".
                var blockedItem = outcome.Batch.Skipped
                    .FirstOrDefault(item => IsWorksharingSkip(item.ErrorCode));
                if (blockedItem is not null)
                {
                    outcome.RollBackChangeSetId = null;
                    outcome.Success = false;
                    outcome.ErrorCode = blockedItem.ErrorCode;
                    outcome.Message = blockedItem.Reason;
                    return;
                }

                // A dry run must never change state: do not close the set. Only a
                // real undo that found nothing to revert closes it so the next
                // unnamed undo_last can reach earlier change sets.
                outcome.RollBackChangeSetId = dryRun ? null : details.Header.Id;
                outcome.Success = false;
                outcome.ErrorCode = "nothing_to_undo";
                outcome.Message = dryRun
                    ? "Nothing would be reverted; the model already matches the recorded " +
                      "previous state. The change set stays applied (dry run)."
                    : "Nothing was reverted; the model already matches the " +
                      "recorded previous state. The change set was closed.";
                return;
            }

            outcome.RollBackChangeSetId = null;
            outcome.Success = false;
            outcome.ErrorCode = "undo_conflict";
            outcome.Message = "Nothing could be reverted: the recorded values were changed " +
                              "after the operation. Pass force=true to restore them anyway.";
            return;
        }

        if (notReverted > 0)
        {
            // A partially reverted set must stay applied: the remaining failed items still
            // need to be undoable later (marking it rolled back would hide them).
            outcome.RollBackChangeSetId = null;
            outcome.Success = false;
            outcome.ErrorCode = "undo_partial";
            outcome.Message = outcome.AffectedCount + " item(s) reverted, " + notReverted +
                              " failed. The change set stays applied so a later " +
                              "undo_last (or force=true) can finish the job. See failed[].";
        }
    }

    private static (bool Ok, string? ErrorCode, string? Reason) Revert(
        Document doc,
        Element element,
        ChangeItem item,
        bool force)
    {
        var key = item.ParameterName ?? string.Empty;

        if (key is "view_template" or "view_range" or "crop" or "visibility"
            or "viewRange" or "template" or "view_template_inclusion" or "scope_box")
        {
            if (element is not View view)
                return (false, "unsupported_element", "Element " + item.ElementId + " is not a view.");

            return key switch
            {
                "view_template" or "template" => RevertTemplate(doc, view, item, force),
                "view_range" or "viewRange" => RevertViewRange(view, item, force),
                "crop" => RevertCrop(view, item, force),
                "view_template_inclusion" => RevertTemplateInclusion(view, item, force),
                "scope_box" => RevertScopeBox(view, item, force),
                _ => RevertVisibility(doc, view, item, force)
            };
        }

        if (key == "filter_name")
        {
            if (element is not ParameterFilterElement filter)
            {
                return (false, "unsupported_element",
                    "Element " + item.ElementId + " is not a view filter.");
            }

            if (string.Equals(filter.Name, item.OldValue, StringComparison.Ordinal))
                return (false, null, "The filter already has the previous name (no-op).");

            if (!force && !string.Equals(filter.Name, item.NewValue, StringComparison.Ordinal))
            {
                return (false, "undo_conflict",
                    "The filter is named '" + filter.Name + "' instead of '" + item.NewValue +
                    "' — it was renamed after the operation (conflict).");
            }

            if (string.IsNullOrWhiteSpace(item.OldValue))
                return (false, "invalid_change_item", "The recorded previous name is empty.");

            filter.Name = item.OldValue;
            return (true, null, null);
        }

        if (key.StartsWith("instance:", StringComparison.Ordinal)
            || key.StartsWith("type:", StringComparison.Ordinal))
        {
            return RevertParameter(doc, element, item, force);
        }

        return (false, null, "Unknown change item type '" + key + "' — skipped.");
    }

    private static (bool, string?, string?) RevertTemplate(
        Document doc,
        View view,
        ChangeItem item,
        bool force)
    {
        var current = IdText(view.ViewTemplateId);
        if (string.Equals(current, item.OldValue ?? string.Empty, StringComparison.Ordinal))
            return (false, null, "The template already matches the recorded previous state.");

        if (!force && !string.Equals(current, item.NewValue, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "The template of '" + view.Name + "' changed after the operation (conflict).");
        }

        if (string.IsNullOrWhiteSpace(item.OldValue))
        {
            view.ViewTemplateId = ElementId.InvalidElementId;
            return (true, null, null);
        }

        if (!long.TryParse(item.OldValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return (false, "invalid_change_item", "The recorded template id is not a number.");

        if (doc.GetElement(ElementIdCompat.Create(id)) is not View template || !template.IsTemplate)
        {
            return (false, "template_not_found",
                "The previous template " + id + " no longer exists in the document.");
        }

        view.ViewTemplateId = ElementIdCompat.Create(id);
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertTemplateInclusion(
        View template,
        ChangeItem item,
        bool force)
    {
        if (!template.IsTemplate)
            return (false, "unsupported_element",
                "Element " + item.ElementId + " is not a view template.");

        var oldIds = ParseIdSet(item.OldValue);
        var newIds = ParseIdSet(item.NewValue);
        if (oldIds is null || newIds is null)
            return (false, "invalid_change_item",
                "The recorded template inclusion is not a list of parameter ids.");

        var current = new HashSet<long>(
            template.GetNonControlledTemplateParameterIds().Select(id => id.GetValue()));
        if (current.SetEquals(oldIds))
            return (false, null, "The template inclusion already matches the recorded previous state.");

        if (!force && !current.SetEquals(newIds))
        {
            return (false, "undo_conflict",
                "The template inclusion of '" + template.Name +
                "' changed after the operation (conflict).");
        }

        var restored = template.GetTemplateParameterIds()
            .Where(id => oldIds.Contains(id.GetValue()))
            .ToList();
        template.SetNonControlledTemplateParameterIds(restored);
        return (true, null, null);
    }
}
