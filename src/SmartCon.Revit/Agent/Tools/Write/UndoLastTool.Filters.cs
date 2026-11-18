using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed partial class UndoLastTool
{
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