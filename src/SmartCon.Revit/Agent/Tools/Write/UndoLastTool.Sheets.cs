using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Undo of sheet change items: created sheets and viewports are deleted,
/// viewport centers are restored, and number/name reverts are planned as one
/// batch (a swap of two sheet numbers must go through temporary values).
/// </summary>
public sealed partial class UndoLastTool
{
    private static (bool Ok, string? ErrorCode, string? Reason) RevertSheetChange(
        Document doc,
        Element? element,
        ChangeItem item,
        string key,
        bool force)
    {
        if (element is null)
        {
            return key == "sheet_created"
                ? (false, null, "The sheet no longer exists — already removed.")
                : (false, "element_not_found", "The element no longer exists in the document.");
        }

        try
        {
            if (key is "sheet_created" or "viewport_created")
            {
                if (key == "sheet_created" && element is not ViewSheet)
                    return (false, "unsupported_element",
                        "Element " + item.ElementId + " is not a sheet.");
                if (key == "viewport_created" && element is not Viewport)
                    return (false, "unsupported_element",
                        "Element " + item.ElementId + " is not a viewport.");

                doc.Delete(element.Id);
                return (true, null, null);
            }

            if (key == "viewport_center")
            {
                if (element is not Viewport)
                    return (false, "unsupported_element",
                        "Element " + item.ElementId + " is not a viewport.");

                return RevertViewportCenter((Viewport)element, item, force);
            }

            return (false, null, "Unknown change item type '" + key + "' — skipped.");
        }
        catch (Exception ex)
        {
            return (false, "revert_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static (bool Ok, string? ErrorCode, string? Reason) RevertViewportCenter(
        Viewport viewport,
        ChangeItem item,
        bool force)
    {
        if (!AgentSheetWriteHelper.TryParseCenter(item.OldValue, out var oldMmX, out var oldMmY))
            return (false, "invalid_change_item",
                "The recorded previous center is not 'x;y' millimetres.");

        double currentMmX;
        double currentMmY;
        try
        {
            var current = viewport.GetBoxCenter();
            currentMmX = AgentUnitsFormat.ToMillimeters(current.X);
            currentMmY = AgentUnitsFormat.ToMillimeters(current.Y);
        }
        catch (Exception ex)
        {
            return (false, "revert_failed",
                "The viewport has no box center on a sheet: " + ex.Message);
        }

        if (Math.Abs(currentMmX - oldMmX) < 0.5 && Math.Abs(currentMmY - oldMmY) < 0.5)
            return (false, null, "The viewport is already at the previous center (no-op).");

        if (!force
            && AgentSheetWriteHelper.TryParseCenter(item.NewValue, out var newMmX, out var newMmY)
            && (Math.Abs(currentMmX - newMmX) > 0.5 || Math.Abs(currentMmY - newMmY) > 0.5))
        {
            return (false, "undo_conflict",
                "The viewport center is " +
                AgentSheetWriteHelper.CenterText(currentMmX, currentMmY) +
                " mm instead of the recorded " + item.NewValue +
                " — it was moved after the operation (conflict).");
        }

        var pointX = AgentSheetWriteHelper.MillimetersToInternal(oldMmX);
        var pointY = AgentSheetWriteHelper.MillimetersToInternal(oldMmY);
        viewport.SetBoxCenter(new XYZ(pointX, pointY, 0));
        return (true, null, null);
    }

    /// <summary>
    /// Restores sheet numbers and names as one batch. Reverts run through the
    /// rename planner: two sheets that swapped numbers cannot be restored
    /// one by one, so temporary values break the cycle first.
    /// </summary>
    private static void RevertSheetRenames(
        Document doc,
        IReadOnlyList<(ChangeItem Item, ViewSheet Sheet)> items,
        WriteOutcome outcome,
        bool force)
    {
        if (items.Count == 0)
            return;

        RevertSheetValues(
            doc,
            items.Where(pair => pair.Item.ParameterName == "sheet_number").ToList(),
            sheet => sheet.SheetNumber ?? string.Empty,
            (sheet, value) => sheet.SheetNumber = value,
            outcome,
            force,
            "number");

        RevertSheetValues(
            doc,
            items.Where(pair => pair.Item.ParameterName == "sheet_name").ToList(),
            sheet => sheet.Name ?? string.Empty,
            (sheet, value) => sheet.Name = value,
            outcome,
            force,
            "name");
    }

    private static void RevertSheetValues(
        Document doc,
        IReadOnlyList<(ChangeItem Item, ViewSheet Sheet)> items,
        Func<ViewSheet, string> getter,
        Action<ViewSheet, string> setter,
        WriteOutcome outcome,
        bool force,
        string valueKind)
    {
        var pending = new List<(ChangeItem Item, ViewSheet Sheet, string Current)>();
        foreach (var (item, sheet) in items)
        {
            var report = CreateReport(item);
            var current = getter(sheet);

            if (string.Equals(current, item.OldValue, StringComparison.Ordinal))
            {
                report.Reason = "The sheet already has the previous " + valueKind + " (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            if (!force && !string.Equals(current, item.NewValue, StringComparison.Ordinal))
            {
                report.ErrorCode = "undo_conflict";
                report.Reason = "The sheet " + valueKind + " is '" + current +
                                "' instead of '" + item.NewValue +
                                "' — it was changed after the operation (conflict).";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (string.IsNullOrEmpty(item.OldValue))
            {
                report.ErrorCode = "invalid_change_item";
                report.Reason = "The recorded previous " + valueKind + " is empty.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            pending.Add((item, sheet, current));
        }

        if (pending.Count == 0)
            return;

        var entries = pending
            .Select(pair => new AgentRenamePlanner.Entry
            {
                Id = pair.Item.ElementId ?? 0,
                CurrentName = pair.Current,
                NewName = pair.Item.OldValue ?? string.Empty
            })
            .ToList();

        // The values held by the batch itself are released by the planner, so
        // the document list must carry every sheet, including the pending ones.
        var documentValues = new List<string>();
        foreach (var sheet in AgentSheetReader.Collect(doc, excludePlaceholders: false))
            documentValues.Add(getter(sheet));

        var plan = AgentRenamePlanner.Build(entries, documentValues);
        var succeeded = new HashSet<long>();

        foreach (var entry in plan.TempFirst)
        {
            if (doc.GetElement(ElementIdCompat.Create(entry.Id)) is ViewSheet temp)
                setter(temp, AgentRenamePlanner.TempName());
        }

        foreach (var entry in plan.Order)
        {
            var pair = pending.First(candidate => (candidate.Item.ElementId ?? 0) == entry.Id);
            var report = CreateReport(pair.Item);
            if (doc.GetElement(ElementIdCompat.Create(entry.Id)) is not ViewSheet sheet)
            {
                report.ErrorCode = "element_not_found";
                report.Reason = "The sheet no longer exists in the document.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            try
            {
                setter(sheet, entry.NewName);
                outcome.Batch.Changed.Add(report);
                outcome.AffectedCount++;
                succeeded.Add(entry.Id);
            }
            catch (Exception ex)
            {
                report.ErrorCode = "revert_failed";
                report.Reason = ex.Message;
                outcome.Batch.Failed.Add(report);
            }
        }

        var planned = new HashSet<long>(plan.Order.Select(entry => entry.Id));
        foreach (var pair in pending.Where(pair => !planned.Contains(pair.Item.ElementId ?? 0)))
        {
            var report = CreateReport(pair.Item);
            report.ErrorCode = "undo_conflict";
            report.Reason = "The previous " + valueKind + " '" + pair.Item.OldValue +
                            "' is still used by another sheet; the " + valueKind +
                            " was not restored.";
            outcome.Batch.Failed.Add(report);
        }

        foreach (var entry in plan.TempFirst)
        {
            if (succeeded.Contains(entry.Id))
                continue;

            if (doc.GetElement(ElementIdCompat.Create(entry.Id)) is not ViewSheet sheet)
                continue;

            var current = getter(sheet);
            if (!current.StartsWith(AgentRenamePlanner.TempPrefix, StringComparison.Ordinal))
                continue;

            var pair = pending.First(candidate => (candidate.Item.ElementId ?? 0) == entry.Id);
            try
            {
                setter(sheet, pair.Current);
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "Sheet " + entry.Id + " is left with the temporary " + valueKind + " '" +
                    current + "': " + ex.Message +
                    " [Action: set the " + valueKind + " manually in Revit]");
            }
        }
    }
}
