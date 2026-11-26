using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Shared pieces of the destructive delete tools: publishing the dry-run name
/// list, refusing an apply whose confirmNames does not match a fresh
/// recomputation of the targets (S.5), and the batch delete with per-item
/// reports (pinned / not deleted / failed).
/// </summary>
internal static class AgentDeleteHelper
{
    internal class DeleteTarget
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    internal static JsonElement? GetConfirmNames(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("confirmNames", out var value))
        {
            return value;
        }

        return null;
    }

    internal static void PublishNames(WriteOutcome outcome, IReadOnlyList<string> names)
    {
        var sorted = new List<string>(names);
        sorted.Sort(StringComparer.Ordinal);
        outcome.Data["names"] = sorted;
    }

    /// <summary>
    /// Fails the outcome with the typed confirmation error; the caller must
    /// return immediately so the model stays untouched.
    /// </summary>
    internal static void RefuseConfirmation(WriteOutcome outcome, AgentDeleteConfirmResult confirm)
    {
        outcome.Success = false;
        outcome.ErrorCode = confirm.ErrorCode;
        outcome.Message = confirm.Message;
        outcome.Data["expected"] = confirm.Expected;
        outcome.Data["got"] = confirm.Got;
        if (confirm.Missing.Count > 0)
            outcome.Data["missing"] = confirm.Missing;
        if (confirm.Unexpected.Count > 0)
            outcome.Data["unexpected"] = confirm.Unexpected;
        outcome.Data["undoSupported"] = false;
    }

    /// <summary>
    /// Deletes the targets in one <c>Document.Delete</c> call. Returns the
    /// number of requested targets Revit reported as deleted; per-item failures
    /// (pinned, disappeared, not reported, Revit refused) land in failed[].
    /// The deleted set also contains cascaded dependents.
    /// </summary>
    internal static int DeleteBatch(
        Document doc,
        List<DeleteTarget> targets,
        WriteOutcome outcome,
        string what,
        CancellationToken ct,
        AgentWorksharingScope? worksharing = null)
    {
        var deletable = new List<DeleteTarget>(targets.Count);
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            if (worksharing is not null
                && worksharing.TryGetBlock(ElementIdCompat.Create(target.Id), out var block))
            {
                outcome.Batch.Skipped.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    ErrorCode = block.Code,
                    Reason = block.Reason
                });
                continue;
            }

            var element = doc.GetElement(ElementIdCompat.Create(target.Id));
            if (element is null)
            {
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    ErrorCode = "element_not_found",
                    Reason = "Element " + target.Id + " disappeared before the deletion."
                });
                continue;
            }

            if (element.Pinned)
            {
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    ErrorCode = "element_pinned",
                    Reason = "Element '" + target.Name + "' is pinned; unpin it before deleting."
                });
                continue;
            }

            deletable.Add(target);
        }

        if (deletable.Count == 0)
            return 0;

        var deletedIds = new HashSet<long>();
        try
        {
            var ids = new List<ElementId>(deletable.Count);
            foreach (var target in deletable)
                ids.Add(ElementIdCompat.Create(target.Id));

            foreach (var deletedId in doc.Delete(ids))
                deletedIds.Add(deletedId.GetValue());
        }
        catch (Exception ex)
        {
            foreach (var target in deletable)
            {
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    ErrorCode = "delete_failed",
                    Reason = "Revit refused to delete '" + target.Name + "': " + ex.Message
                });
            }
        }

        var deletedCount = 0;
        foreach (var target in deletable)
        {
            if (deletedIds.Contains(target.Id))
            {
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    NewValue = "deleted"
                });
                deletedCount++;
            }
            else
            {
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    ErrorCode = "element_not_deleted",
                    Reason = "Revit did not report element " + target.Id +
                             " as deleted; re-run the dry run to see the current state."
                });
            }
        }

        outcome.Data["deletedCount"] = deletedCount;
        outcome.Data["dependentsDeleted"] = Math.Max(0, deletedIds.Count - deletedCount);
        return deletedCount;
    }

    internal static void MarkDestructiveApplied(WriteOutcome outcome, string what)
    {
        outcome.SuppressChangeSet = true;
        outcome.Data["undoSupported"] = false;
        outcome.Warnings.Add(
            "Deleted " + what + " cannot be restored by undo_last; use the Revit Undo " +
            "command or a backup if you need them back.");
    }
}
