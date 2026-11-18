using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Undo of name renames (view_name / level_name). A swap or cycle must be reverted
/// as one batch with temporary names, otherwise the first restore collides.
/// </summary>
public sealed partial class UndoLastTool
{
    private static void RevertRenames(
        Document doc,
        IReadOnlyList<(ChangeItem Item, Element Element)> renames,
        WriteOutcome outcome,
        bool force)
    {
        if (renames.Count == 0)
            return;

        var views = new List<(ChangeItem Item, View View)>();
        var levels = new List<(ChangeItem Item, Level Level)>();
        foreach (var pair in renames)
        {
            if (pair.Element is View view)
                views.Add((pair.Item, view));
            else if (pair.Element is Level level)
                levels.Add((pair.Item, level));
        }

        RevertViewRenames(doc, views, outcome, force);
        RevertLevelRenames(doc, levels, outcome, force);
    }

    private static void RevertViewRenames(
        Document doc,
        IReadOnlyList<(ChangeItem Item, View View)> items,
        WriteOutcome outcome,
        bool force)
    {
        var pending = new List<(ChangeItem Item, View View, string Current)>();
        foreach (var (item, view) in items)
        {
            var report = CreateReport(item);

            // Already at the previous name (e.g. renamed back by the user): nothing to do,
            // and it must not be reported as a conflict.
            if (string.Equals(view.Name, item.OldValue, StringComparison.Ordinal))
            {
                report.Reason = "The view already has the previous name (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            if (!force && !string.Equals(view.Name, item.NewValue, StringComparison.Ordinal))
            {
                report.ErrorCode = "undo_conflict";
                report.Reason = "The view is named '" + view.Name + "' instead of '" +
                                item.NewValue + "' — it was changed after the operation (conflict).";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (string.IsNullOrEmpty(item.OldValue))
            {
                report.ErrorCode = "invalid_change_item";
                report.Reason = "The recorded previous name is empty.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            pending.Add((item, view, view.Name));
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

        var documentNames = AgentViewWriteHelper.CollectViewNamesByName(doc).Keys;
        var plan = AgentRenamePlanner.Build(entries, documentNames);
        var succeeded = new HashSet<long>();

        foreach (var entry in plan.TempFirst)
        {
            if (doc.GetElement(ElementIdCompat.Create(entry.Id)) is View temp)
                temp.Name = AgentRenamePlanner.TempName();
        }

        foreach (var entry in plan.Order)
        {
            var pair = pending.First(candidate => (candidate.Item.ElementId ?? 0) == entry.Id);
            var report = CreateReport(pair.Item);
            var view = doc.GetElement(ElementIdCompat.Create(entry.Id)) as View;
            if (view is null)
            {
                report.ErrorCode = "element_not_found";
                report.Reason = "The view no longer exists in the document.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            try
            {
                view.Name = entry.NewName;
                report.Name = entry.NewName;
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
            report.Reason = "The previous name '" + pair.Item.OldValue + "' is still used by " +
                            "another element; the rename was not restored.";
            outcome.Batch.Failed.Add(report);
        }

        RestoreViewTempNames(doc, plan, succeeded, outcome);
    }

    private static void RestoreViewTempNames(
        Document doc,
        AgentRenamePlanner.Plan plan,
        HashSet<long> succeeded,
        WriteOutcome outcome)
    {
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
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "View " + entry.Id + " is left with the temporary name '" + view.Name +
                    "': " + ex.Message + " [Action: rename it manually in Revit]");
            }
        }
    }

    private static void RevertLevelRenames(
        Document doc,
        IReadOnlyList<(ChangeItem Item, Level Level)> items,
        WriteOutcome outcome,
        bool force)
    {
        var pending = new List<(ChangeItem Item, Level Level, string Current)>();
        foreach (var (item, level) in items)
        {
            var report = CreateReport(item);

            if (string.Equals(level.Name, item.OldValue, StringComparison.Ordinal))
            {
                report.Reason = "The level already has the previous name (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            if (!force && !string.Equals(level.Name, item.NewValue, StringComparison.Ordinal))
            {
                report.ErrorCode = "undo_conflict";
                report.Reason = "The level is named '" + level.Name + "' instead of '" +
                                item.NewValue + "' — it was changed after the operation (conflict).";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (string.IsNullOrEmpty(item.OldValue))
            {
                report.ErrorCode = "invalid_change_item";
                report.Reason = "The recorded previous name is empty.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            pending.Add((item, level, level.Name));
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

        var documentNames = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Select(level => level.Name)
            .ToList();

        var plan = AgentRenamePlanner.Build(entries, documentNames);
        var succeeded = new HashSet<long>();

        foreach (var entry in plan.TempFirst)
        {
            if (doc.GetElement(ElementIdCompat.Create(entry.Id)) is Level temp)
                temp.Name = AgentRenamePlanner.TempName();
        }

        foreach (var entry in plan.Order)
        {
            var pair = pending.First(candidate => (candidate.Item.ElementId ?? 0) == entry.Id);
            var report = CreateReport(pair.Item);
            var level = doc.GetElement(ElementIdCompat.Create(entry.Id)) as Level;
            if (level is null)
            {
                report.ErrorCode = "element_not_found";
                report.Reason = "The level no longer exists in the document.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            try
            {
                level.Name = entry.NewName;
                report.Name = entry.NewName;
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
            report.Reason = "The previous name '" + pair.Item.OldValue + "' is still used by " +
                            "another element; the rename was not restored.";
            outcome.Batch.Failed.Add(report);
        }

        RestoreLevelTempNames(doc, plan, succeeded, outcome);
    }

    private static void RestoreLevelTempNames(
        Document doc,
        AgentRenamePlanner.Plan plan,
        HashSet<long> succeeded,
        WriteOutcome outcome)
    {
        foreach (var entry in plan.TempFirst)
        {
            if (succeeded.Contains(entry.Id))
                continue;

            if (doc.GetElement(ElementIdCompat.Create(entry.Id)) is not Level level)
                continue;

            if (!level.Name.StartsWith(AgentRenamePlanner.TempPrefix, StringComparison.Ordinal))
                continue;

            try
            {
                level.Name = entry.CurrentName;
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "Level " + entry.Id + " is left with the temporary name '" + level.Name +
                    "': " + ex.Message + " [Action: rename it manually in Revit]");
            }
        }
    }
}
