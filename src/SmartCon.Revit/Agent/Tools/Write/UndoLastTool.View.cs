using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed partial class UndoLastTool
{
    /// <summary>
    /// Undo dispatch of the G.6 view items: a view created by duplicate_view
    /// or create_view is deleted (cascade: dependents and viewports go with
    /// it), and the browser folder of set_browser_folder is restored by
    /// writing the recorded value back into the grouping parameter of the
    /// current browser organization.
    /// </summary>
    private static void RecordViewRevert(
        Document doc,
        WriteOutcome outcome,
        ChangeItem item,
        string key,
        bool force)
    {
        var report = CreateReport(item);
        var (reverted, errorCode, reason) =
            RevertViewChange(doc, ResolveElement(doc, item), item, key, force);
        if (reverted)
        {
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;
        }
        else if (errorCode is null)
        {
            report.Reason = reason;
            outcome.Batch.Skipped.Add(report);
        }
        else
        {
            report.ErrorCode = errorCode;
            report.Reason = reason;
            outcome.Batch.Failed.Add(report);
        }
    }

    private static (bool, string?, string?) RevertViewChange(
        Document doc,
        Element? element,
        ChangeItem item,
        string key,
        bool force)
    {
        if (key == "view_created")
        {
            if (element is null)
                return (false, null, "The view no longer exists — already removed.");

            if (element is not View view)
                return (false, "unsupported_element",
                    "Element " + item.ElementId + " is not a view.");

            doc.Delete(view.Id);
            return (true, null, null);
        }

        if (element is null)
            return (false, "element_not_found", "The view no longer exists in the document.");

        if (element is not View folderView)
            return (false, "unsupported_element",
                "Element " + item.ElementId + " is not a view.");

        var organization = folderView is ViewSheet
            ? BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc)
            : BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
        Parameter? parameter = null;
        string? reason = null;
        if (organization is null
            || !SetBrowserFolderTool.TryGetGroupingParameter(
                doc, organization, folderView, out parameter, out reason))
        {
            return (false, "browser_folder_unavailable",
                reason ?? "The document has no browser organization.");
        }

        var current = parameter.AsString() ?? string.Empty;
        if (string.Equals(current, item.OldValue ?? string.Empty, StringComparison.Ordinal))
            return (false, null, "The view is already in the recorded previous folder.");

        if (!force && !string.Equals(current, item.NewValue ?? string.Empty, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "The browser folder of '" + folderView.Name + "' is '" + current +
                "' instead of '" + item.NewValue +
                "' — it was changed after the operation (conflict).");
        }

        parameter.Set(item.OldValue ?? string.Empty);
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertViewRange(View view, ChangeItem item, bool force)
    {
        if (view is not ViewPlan plan)
            return (false, "unsupported_view_type", "View range applies to plan views only.");

        var spec = AgentViewWriteHelper.Deserialize<ViewRangeSpec>(item.OldValue);
        if (spec is null || spec.IsEmpty)
            return (false, null, "The recorded view range is empty — nothing to restore.");

        var current = AgentViewWriteHelper.Serialize(
            AgentViewWriteHelper.ReadViewRange(view) ?? new ViewRangeSpec());
        if (string.Equals(current, item.OldValue, StringComparison.Ordinal))
            return (false, null, "The view range already matches the recorded previous state.");

        if (!force && !string.Equals(current, item.NewValue, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "The view range of '" + view.Name +
                "' changed after the operation (conflict).");
        }

        AgentViewWriteHelper.ApplyViewRange(plan, spec);
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertCrop(View view, ChangeItem item, bool force)
    {
        var state = AgentViewWriteHelper.Deserialize<ViewCropState>(item.OldValue);
        if (state is null)
            return (false, null, "The recorded crop state is empty — nothing to restore.");

        var current = AgentViewWriteHelper.Serialize(AgentViewWriteHelper.ReadCropState(view));
        if (string.Equals(current, item.OldValue, StringComparison.Ordinal))
            return (false, null, "The crop region already matches the recorded previous state.");

        if (!force && !string.Equals(current, item.NewValue, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "The crop region of '" + view.Name + "' changed after the operation (conflict).");
        }

        var warnings = new List<string>();
        AgentViewWriteHelper.ApplyCropState(view, state, warnings);

        // The helper reports rejected extents/shape through warnings; without this
        // check undo returned true while the crop still differed from the record.
        // Compare semantics, not serialized text: a restored sketch may be
        // re-read with equivalent but differently ordered/rounded coordinates.
        var restored = AgentViewWriteHelper.ReadCropState(view);
        var restoredOk = restored.Active == state.Active
                         && restored.Visible == state.Visible
                         && restored.ShapeSet == state.ShapeSet
                         && (state.ShapeSet || SameExtents(restored, state));
        if (!restoredOk)
        {
            var detail = warnings.Count == 0 ? string.Empty : " " + string.Join("; ", warnings);
            return (false, "undo_crop_failed",
                "The crop region did not match the recorded state after the restore." + detail);
        }

        return (true, null, null);
    }

    private static (bool, string?, string?) RevertVisibility(
        Document doc,
        View view,
        ChangeItem item,
        bool force)
    {
        var state = AgentViewWriteHelper.Deserialize<ViewVisibilityState>(item.OldValue);
        if (state is null)
            return (false, null, "The recorded visibility state is empty — nothing to restore.");

        var current = AgentViewWriteHelper.ReadVisibility(doc, view);
        var recordedNew = AgentViewWriteHelper.Deserialize<ViewVisibilityState>(item.NewValue);

        // Visibility is stored as the full hidden set, but only the categories the
        // view can toggle are written back. Restricting the conflict check to that
        // same subset keeps a template edit (or a category the API cannot hide in
        // this view type) from reporting a conflict on a view nobody touched.
        var toggleable = CollectToggleableCategories(doc, view);
        var currentFiltered = Filter(current.HiddenCategoryIds, toggleable);
        var previousFiltered = Filter(state.HiddenCategoryIds, toggleable);

        if (SameSequence(currentFiltered, previousFiltered))
            return (false, null, "Visibility already matches the recorded previous state.");

        if (!force && recordedNew is not null)
        {
            var newFiltered = Filter(recordedNew.HiddenCategoryIds, toggleable);
            if (!SameSequence(currentFiltered, newFiltered))
            {
                return (false, "undo_conflict",
                    "Visibility of '" + view.Name + "' changed after the operation (conflict).");
            }
        }

        AgentViewWriteHelper.ApplyVisibilityState(doc, view, state);
        var restored = Filter(
            AgentViewWriteHelper.ReadVisibility(doc, view).HiddenCategoryIds, toggleable);
        if (!SameSequence(restored, previousFiltered))
        {
            return (false, "undo_visibility_failed",
                "The category visibility did not match the recorded state after the restore " +
                "(some categories reject SetCategoryHidden in this view).");
        }

        return (true, null, null);
    }

    private static bool SameExtents(ViewCropState left, ViewCropState right)
    {
        return SamePoint(left.MinMm, right.MinMm) && SamePoint(left.MaxMm, right.MaxMm);
    }

    private static bool SamePoint(double[]? left, double[]? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (Math.Abs(left[index] - right[index]) > 0.5)
                return false;
        }

        return true;
    }

    private static HashSet<long> CollectToggleableCategories(Document doc, View view)
    {
        var toggleable = new HashSet<long>();
        foreach (Category category in doc.Settings.Categories)
        {
            if (AgentViewSettingsReader.CanToggleCategory(view, category.Id.GetValue()))
                toggleable.Add(category.Id.GetValue());
        }

        return toggleable;
    }

    private static List<long> Filter(IEnumerable<long> ids, HashSet<long> allowed) =>
        ids.Where(allowed.Contains).OrderBy(id => id).ToList();

    private static bool SameSequence(IReadOnlyList<long> left, IReadOnlyList<long> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
                return false;
        }

        return true;
    }
}