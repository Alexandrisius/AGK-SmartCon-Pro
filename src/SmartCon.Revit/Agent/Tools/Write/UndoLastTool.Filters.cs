using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed partial class UndoLastTool
{
    /// <summary>
    /// Undo dispatch of the view-filter change items: a filter created by the
    /// reverted set is deleted, the filter state of a view is restored from the
    /// recorded snapshot.
    /// </summary>
    private static void RecordFilterRevert(
        Document doc,
        WriteOutcome outcome,
        ChangeItem item,
        string key,
        bool force)
    {
        var report = CreateReport(item);
        var element = ResolveElement(doc, item);

        if (key == "filter_created")
        {
            if (element is null)
            {
                report.Reason = "The filter no longer exists — already removed.";
                outcome.Batch.Skipped.Add(report);
                return;
            }

            if (element is not ParameterFilterElement createdFilter)
            {
                report.ErrorCode = "unsupported_element";
                report.Reason = "Element " + item.ElementId + " is not a view filter.";
                outcome.Batch.Failed.Add(report);
                return;
            }

            var (deleted, deleteError, deleteReason) = RevertFilterCreated(doc, createdFilter);
            if (deleted)
            {
                outcome.Batch.Changed.Add(report);
                outcome.AffectedCount++;
            }
            else
            {
                report.ErrorCode = deleteError;
                report.Reason = deleteReason;
                outcome.Batch.Failed.Add(report);
            }

            return;
        }

        if (element is null)
        {
            report.ErrorCode = "element_not_found";
            report.Reason = "The view no longer exists in the document.";
            outcome.Batch.Failed.Add(report);
            return;
        }

        if (element is not View filteredView)
        {
            report.ErrorCode = "unsupported_element";
            report.Reason = "Element " + item.ElementId + " is not a view.";
            outcome.Batch.Failed.Add(report);
            return;
        }

        var (reverted, revertError, revertReason) = RevertFilter(filteredView, item, force);
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
    }

    /// <summary>
    /// Restores the filter state of a view: removes filters that were added after
    /// the recorded state, re-applies recorded visibility and overrides.
    /// </summary>
    private static (bool Ok, string? ErrorCode, string? Reason) RevertFilter(
        View view,
        ChangeItem item,
        bool force)
    {
        var state = AgentFilterWriteHelper.DeserializeState(item.OldValue);
        if (state is null)
            return (false, null, "The recorded filter state is empty — nothing to restore.");

        var current = AgentFilterWriteHelper.SerializeState(
            AgentFilterWriteHelper.ReadState(view));
        if (string.Equals(current, item.OldValue, StringComparison.Ordinal))
            return (false, null, "The view filters already match the recorded previous state.");

        if (!force && !string.Equals(current, item.NewValue, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "View filters of '" + view.Name + "' changed after the operation (conflict).");
        }

        var desired = new Dictionary<long, ViewFilterEntry>(state.Filters.Count);
        foreach (var entry in state.Filters)
            desired[entry.FilterId] = entry;

        foreach (var filterId in view.GetFilters())
        {
            if (!desired.ContainsKey(filterId.GetValue()))
                view.RemoveFilter(filterId);
        }

        foreach (var entry in state.Filters)
        {
            var filterId = ElementIdCompat.Create(entry.FilterId);
            if (view.Document.GetElement(filterId) is not ParameterFilterElement)
                continue;

            if (!IsFilterApplied(view, filterId))
                view.AddFilter(filterId);

            // Both Revit UI checkboxes are part of the recorded state (#400).
            view.SetIsFilterEnabled(filterId, entry.Enabled);
            view.SetFilterVisibility(filterId, entry.Visible);
            view.SetFilterOverrides(filterId, AgentFilterWriteHelper.ToSettings(entry.Overrides));
        }

        return (true, null, null);
    }

    private static bool IsFilterApplied(View view, ElementId filterId)
    {
        foreach (var id in view.GetFilters())
        {
            if (id.GetValue() == filterId.GetValue())
                return true;
        }

        return false;
    }

    /// <summary>Removes a filter element created by the reverted change set.</summary>
    private static (bool Ok, string? ErrorCode, string? Reason) RevertFilterCreated(
        Document doc,
        ParameterFilterElement filter)
    {
        try
        {
            doc.Delete(filter.Id);
            return (true, null, null);
        }
        catch (Exception ex)
        {
            return (false, "filter_delete_failed",
                "Revit could not delete filter '" + filter.Name + "': " + ex.Message);
        }
    }
}