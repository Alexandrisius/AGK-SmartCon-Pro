using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>Where view filters are applied: one index shared by the filter tools.</summary>
internal static class AgentFilterUsage
{
    internal const int MaxViewsScanned = 10000;

    /// <summary>
    /// True when View.GetFilters / AddFilter / SetFilterVisibility are allowed for
    /// the view. Revit throws InvalidOperationException ("The view type does not
    /// support Visibility/Graphics Overriddes") for schedules, legends, sheets and
    /// column schedules; <see cref="View.AreGraphicsOverridesAllowed"/> is the API's
    /// own answer to that question (available since Revit 2019).
    /// </summary>
    internal static bool SupportsFilters(View view)
    {
        try
        {
            if (view is ViewSheet or ViewSchedule)
                return false;

            switch (view.ViewType)
            {
                case ViewType.DrawingSheet:
                case ViewType.Internal:
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.Undefined:
                    return false;
            }

            return view.AreGraphicsOverridesAllowed();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Filter ids of one view; an unsupported view type yields an empty set
    /// instead of an InvalidOperationException.
    /// </summary>
    internal static HashSet<long> FiltersOf(View view)
    {
        var result = new HashSet<long>();
        try
        {
            foreach (var filterId in view.GetFilters())
                result.Add(filterId.GetValue());
        }
        catch (Exception)
        {
            // Schedules, legends and sheets have no Visibility/Graphics filters.
        }

        return result;
    }

    /// <summary>
    /// filterId → views it is applied to. Views without V/G overrides are skipped:
    /// View.GetFilters throws for them and one such view used to fail the whole
    /// tool (external run 6, P0).
    /// </summary>
    internal static Dictionary<long, List<View>> Map(Document doc, CancellationToken ct) =>
        Map(doc, ct, onlyViewId: null);

    /// <summary>
    /// Same map, but a positive <paramref name="onlyViewId"/> scans that one view
    /// instead of the whole document (list_filters(viewId=…)).
    /// </summary>
    internal static Dictionary<long, List<View>> Map(
        Document doc,
        CancellationToken ct,
        long? onlyViewId)
    {
        var map = new Dictionary<long, List<View>>();

        if (onlyViewId is > 0)
        {
            if (doc.GetElement(ElementIdCompat.Create(onlyViewId.Value)) is View single)
                Add(map, single);
            return map;
        }

        var scanned = 0;
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(View)))
        {
            ct.ThrowIfCancellationRequested();
            if (scanned++ >= MaxViewsScanned)
                break;

            if (element is View view)
                Add(map, view);
        }

        return map;
    }

    internal static int Count(Dictionary<long, List<View>> map, long filterId) =>
        map.TryGetValue(filterId, out var views) ? views.Count : 0;

    private static void Add(Dictionary<long, List<View>> map, View view)
    {
        // Skip schedules, legends and sheets up front: a project with dozens of
        // them would otherwise throw an exception per view inside FiltersOf.
        if (!SupportsFilters(view))
            return;

        foreach (var filterId in FiltersOf(view))
        {
            if (!map.TryGetValue(filterId, out var views))
            {
                views = new List<View>();
                map[filterId] = views;
            }

            views.Add(view);
        }
    }
}