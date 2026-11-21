using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Undo of schedule change items: created schedules and placements are
/// deleted; the fields/filters/sorting snapshots recorded in the change item
/// are restored (remove added fields, re-add removed ones and the order;
/// rebuild the filter set; rebuild sorting plus the itemized flag).
/// </summary>
public sealed partial class UndoLastTool
{
    private static (bool Ok, string? ErrorCode, string? Reason) RevertScheduleChange(
        Document doc,
        Element? element,
        ChangeItem item,
        string key,
        bool force)
    {
        if (element is null)
        {
            return key is "schedule_created" or "schedule_placed"
                ? (false, null, "The element no longer exists — already removed.")
                : (false, "element_not_found", "The schedule no longer exists in the document.");
        }

        try
        {
            if (key is "schedule_created" or "schedule_placed")
            {
                if (key == "schedule_created" && element is not ViewSchedule)
                    return (false, "unsupported_element",
                        "Element " + item.ElementId + " is not a schedule.");
                if (key == "schedule_placed" && element is not ScheduleSheetInstance)
                    return (false, "unsupported_element",
                        "Element " + item.ElementId + " is not a schedule placement.");

                doc.Delete(element.Id);
                return (true, null, null);
            }

            if (key == "schedule_moved")
                return element is ScheduleSheetInstance instance
                    ? RevertScheduleMove(instance, item, force)
                    : (false, "unsupported_element",
                        "Element " + item.ElementId + " is not a schedule placement.");

            if (element is not ViewSchedule schedule)
                return (false, "unsupported_element",
                    "Element " + item.ElementId + " is not a schedule.");

            var definition = schedule.Definition;
            return key switch
            {
                "schedule_fields" => RevertScheduleFields(doc, definition, item, force),
                "schedule_totals" => RevertScheduleTotals(definition, item, force),
                "schedule_filters" => RevertScheduleFilters(definition, item, force),
                "schedule_sort" => RevertScheduleSortState(definition, item, force),
                _ => (false, null, "Unknown change item type '" + key + "' — skipped.")
            };
        }
        catch (Exception ex)
        {
            return (false, "revert_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static (bool, string?, string?) RevertScheduleMove(
        ScheduleSheetInstance instance,
        ChangeItem item,
        bool force)
    {
        if (!AgentSheetWriteHelper.TryParseCenter(item.OldValue, out var oldMmX, out var oldMmY))
            return (false, "invalid_change_item",
                "The recorded previous point is not 'x;y' millimetres.");

        double currentMmX;
        double currentMmY;
        try
        {
            var current = instance.Point;
            currentMmX = AgentUnitsFormat.ToMillimeters(current.X);
            currentMmY = AgentUnitsFormat.ToMillimeters(current.Y);
        }
        catch (Exception ex)
        {
            return (false, "revert_failed",
                "The placement point is unreadable: " + ex.Message);
        }

        if (Math.Abs(currentMmX - oldMmX) < 0.5 && Math.Abs(currentMmY - oldMmY) < 0.5)
            return (false, null, "The schedule is already at the previous point (no-op).");

        if (!force
            && AgentSheetWriteHelper.TryParseCenter(item.NewValue, out var newMmX, out var newMmY)
            && (Math.Abs(currentMmX - newMmX) > 0.5 || Math.Abs(currentMmY - newMmY) > 0.5))
        {
            return (false, "undo_conflict",
                "The schedule placement is " +
                AgentSheetWriteHelper.CenterText(currentMmX, currentMmY) +
                " mm instead of the recorded " + item.NewValue +
                " — it was moved after the operation (conflict).");
        }

        var pointX = AgentSheetWriteHelper.MillimetersToInternal(oldMmX);
        var pointY = AgentSheetWriteHelper.MillimetersToInternal(oldMmY);
        instance.Point = new XYZ(pointX, pointY, 0);
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertScheduleTotals(
        ScheduleDefinition definition,
        ChangeItem item,
        bool force)
    {
        var oldState = AgentScheduleWriteHelper.DeserializeTotals(item.OldValue);
        var newState = AgentScheduleWriteHelper.DeserializeTotals(item.NewValue);
        if (oldState is null || newState is null)
            return (false, "invalid_change_item",
                "The recorded totals snapshot is not the expected JSON.");

        var currentJson = AgentScheduleWriteHelper.SerializeTotals(definition);
        if (currentJson == item.OldValue)
            return (false, null, "The schedule already has the previous totals (no-op).");

        if (!force && currentJson != item.NewValue)
            return (false, "undo_conflict",
                "The schedule totals changed after the operation (conflict).");

        try
        {
            // The sub-flags may only be touched while the grand total row is on
            // ("Display of a grand total row is not enabled"): enable the row
            // first when the restored state needs it, set the sub-flags, then
            // drop the row when it was off.
            var needsRow = oldState.GrandTotal || oldState.GrandTotalCount
                                                 || oldState.ShowGrandTotalTitle;
            if (needsRow)
                definition.ShowGrandTotal = true;
            definition.ShowGrandTotalCount = oldState.GrandTotalCount;
            definition.ShowGrandTotalTitle = oldState.ShowGrandTotalTitle;
            if (oldState.ShowGrandTotalTitle)
                definition.GrandTotalTitle = oldState.GrandTotalTitle;
            definition.ShowGrandTotal = oldState.GrandTotal;
            foreach (var fieldState in oldState.Fields)
            {
                var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(
                    definition, fieldState.Name);
                var field = fieldId is null
                    ? null
                    : AgentScheduleWriteHelper.SafeGetField(definition, fieldId);
                if (field is null)
                    continue;

                if (AgentScheduleWriteHelper.TryParseDisplayMode(fieldState.Mode, out var mode))
                    field.DisplayType = mode;
            }

            return (true, null, null);
        }
        catch (Exception ex)
        {
            return (false, "revert_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static (bool, string?, string?) RevertScheduleFields(
        Document doc,
        ScheduleDefinition definition,
        ChangeItem item,
        bool force)
    {
        var oldStates = AgentScheduleWriteHelper.DeserializeFieldStates(item.OldValue);
        var newStates = AgentScheduleWriteHelper.DeserializeFieldStates(item.NewValue);
        var withHidden = oldStates is not null && newStates is not null;
        var oldNames = oldStates is not null
            ? oldStates.Select(state => state.Name).ToList()
            : DeserializeStrings(item.OldValue);
        var newNames = newStates is not null
            ? newStates.Select(state => state.Name).ToList()
            : DeserializeStrings(item.NewValue);
        if (oldNames is null || newNames is null)
            return (false, "invalid_change_item",
                "The recorded field list is not a JSON array of names.");

        var current = AgentScheduleWriteHelper.CurrentFieldNames(definition);
        var alreadyRestored = ListsEqual(current, oldNames)
            && (!withHidden || HiddenMatches(definition, oldStates!));
        if (alreadyRestored)
            return (false, null, "The schedule already has the previous fields (no-op).");

        if (!force && !ListsEqual(current, newNames))
            return (false, "undo_conflict",
                "The schedule fields changed after the operation (conflict).");

        // Remove the fields added by the operation, then re-add removed ones.
        var keep = new HashSet<string>(oldNames, StringComparer.OrdinalIgnoreCase);
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = AgentScheduleWriteHelper.SafeGetField(definition, fieldId);
            if (field is null)
                continue;

            if (!keep.Contains(AgentScheduleWriteHelper.SafeFieldName(field)))
                definition.RemoveField(fieldId);
        }

        var present = new HashSet<string>(
            AgentScheduleWriteHelper.CurrentFieldNames(definition), StringComparer.OrdinalIgnoreCase);
        foreach (var name in oldNames)
        {
            if (present.Contains(name))
                continue;

            if (AgentScheduleWriteHelper.TryFindSchedulableField(doc, definition, name, out var field))
                definition.AddField(field!);
        }

        // Restore the recorded column order where the fields still exist.
        var order = new List<ScheduleFieldId>();
        foreach (var name in oldNames)
        {
            var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, name);
            if (fieldId is not null)
                order.Add(fieldId);
        }

        if (order.Count == definition.GetFieldCount())
            definition.SetFieldOrder(order);

        if (withHidden)
        {
            foreach (var state in oldStates!)
            {
                var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, state.Name);
                var field = fieldId is null
                    ? null
                    : AgentScheduleWriteHelper.SafeGetField(definition, fieldId);
                if (field is null)
                    continue;

                try
                {
                    field.IsHidden = state.Hidden;
                }
                catch (Exception)
                {
                    // A field Revit refuses to hide stays visible.
                }
            }
        }

        return (true, null, null);
    }

    private static bool HiddenMatches(
        ScheduleDefinition definition,
        IReadOnlyList<ScheduleFieldStateRecord> states)
    {
        foreach (var state in states)
        {
            var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, state.Name);
            var field = fieldId is null
                ? null
                : AgentScheduleWriteHelper.SafeGetField(definition, fieldId);
            if (field is null)
                return false;

            try
            {
                if (field.IsHidden != state.Hidden)
                    return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        return true;
    }

    private static (bool, string?, string?) RevertScheduleFilters(
        ScheduleDefinition definition,
        ChangeItem item,
        bool force)
    {
        if (string.IsNullOrWhiteSpace(item.OldValue))
            return (false, "invalid_change_item", "The recorded filter snapshot is empty.");

        var currentJson = AgentScheduleWriteHelper.SerializeFilters(
            definition, AgentScheduleWriteHelper.FieldNamesByFieldId(definition));
        if (currentJson == item.OldValue)
            return (false, null, "The schedule already has the previous filters (no-op).");

        if (!force && currentJson != item.NewValue)
            return (false, "undo_conflict",
                "The schedule filters changed after the operation (conflict).");

        definition.ClearFilters();
        SetScheduleFilterTool.RestoreFilters(definition, item.OldValue!);
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertScheduleSortState(
        ScheduleDefinition definition,
        ChangeItem item,
        bool force)
    {
        if (string.IsNullOrWhiteSpace(item.OldValue))
            return (false, "invalid_change_item", "The recorded sorting snapshot is empty.");

        var currentJson = AgentScheduleWriteHelper.SerializeSort(definition);
        if (currentJson == item.OldValue)
            return (false, null, "The schedule already has the previous sorting (no-op).");

        if (!force && currentJson != item.NewValue)
            return (false, "undo_conflict",
                "The schedule sorting changed after the operation (conflict).");

        SetScheduleSortTool.RestoreSort(definition, item.OldValue!);
        return (true, null, null);
    }

    private static List<string>? DeserializeStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ListsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
