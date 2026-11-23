using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Own view filters versus a view template that includes "V/G Overrides Filters"
/// (VIS_GRAPHICS_FILTERS). Revit hands the filter list of a view to such a
/// template every time the template takes control: on assignment, on any later
/// edit of the template and on turning the inclusion on. A filter added to a
/// templated view is accepted, but it lives only until that moment, so the
/// tools must name the filters that are about to go instead of reporting a
/// clean change.
/// </summary>
internal static class AgentTemplateFilterImpact
{
    /// <summary>
    /// What to do instead of losing the filters: add them to the template (every
    /// view of the template keeps them) or detach "filters" from the template
    /// and assign the template BEFORE creating per-view filters.
    /// </summary>
    internal static string OrderHint(long templateId) =>
        "Revit: a view template that includes 'V/G Overrides Filters' owns the filter list " +
        "of its views. To keep the filters for every view of the template, add them to the " +
        "template (viewIds=[" + templateId + "]); to keep per-view filters, detach " +
        "'filters' from the template first (set_view_template_inclusion(parameters=[" +
        "\"filters\"], included=false)) and assign the template BEFORE creating view filters.";

    internal static bool ControlsFilters(View template) =>
        AgentViewSettingsReader.IsTemplateParameterControlled(
            template, BuiltInParameter.VIS_GRAPHICS_FILTERS);

    /// <summary>
    /// Filters the view owns itself: the applied ones minus the list of the
    /// template that currently controls them.
    /// </summary>
    internal static HashSet<long> OwnFilterIds(Document doc, View view)
    {
        var own = AgentFilterUsage.FiltersOf(view);
        var currentId = view.ViewTemplateId;
        if (currentId is null || currentId == ElementId.InvalidElementId)
            return own;

        if (doc.GetElement(currentId) is View current && ControlsFilters(current))
            own.ExceptWith(AgentFilterUsage.FiltersOf(current));

        return own;
    }

    /// <summary>
    /// Names of the view's own filters that disappear when the view receives
    /// <paramref name="template"/>; empty when the template does not control
    /// filters or keeps every own filter.
    /// </summary>
    internal static List<string> FiltersLostOnAssignment(Document doc, View view, View template)
    {
        var lost = new List<string>();
        if (!AgentFilterUsage.SupportsFilters(view) || !ControlsFilters(template))
            return lost;

        var kept = AgentFilterUsage.FiltersOf(template);
        foreach (var id in OwnFilterIds(doc, view))
        {
            if (!kept.Contains(id))
                lost.Add(NameOf(doc, id));
        }

        lost.Sort(StringComparer.OrdinalIgnoreCase);
        return lost;
    }

    /// <summary>
    /// Views that use <paramref name="template"/> and own filters beyond its
    /// list — they lose them when the template changes. Read BEFORE the change.
    /// </summary>
    internal static List<(long ViewId, string ViewName, List<string> Filters)> ViewsLosingOwnFilters(
        Document doc,
        View template)
    {
        var result = new List<(long, string, List<string>)>();
        var templateFilters = AgentFilterUsage.FiltersOf(template);
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(View)))
        {
            if (element is not View view || view.IsTemplate || view.ViewTemplateId != template.Id)
                continue;

            if (!AgentFilterUsage.SupportsFilters(view))
                continue;

            var lost = new List<string>();
            foreach (var id in OwnFilterIds(doc, view))
            {
                if (!templateFilters.Contains(id))
                    lost.Add(NameOf(doc, id));
            }

            if (lost.Count == 0)
                continue;

            lost.Sort(StringComparer.OrdinalIgnoreCase);
            result.Add((view.Id.GetValue(), view.Name, lost));
        }

        result.Sort((left, right) => left.Item1.CompareTo(right.Item1));
        return result;
    }

    /// <summary>
    /// One summary line for a template change: how many views lose which own filters.
    /// Null when nothing is lost.
    /// </summary>
    internal static string? DescribeLostFilters(
        string lead,
        IReadOnlyList<(long ViewId, string ViewName, List<string> Filters)> views,
        int totalViewCount)
    {
        if (views.Count == 0)
            return null;

        var byFilter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in views)
        {
            foreach (var filter in entry.Filters)
                byFilter[filter] = byFilter.TryGetValue(filter, out var count) ? count + 1 : 1;
        }

        var filters = byFilter
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Value == views.Count
                ? "'" + pair.Key + "'"
                : "'" + pair.Key + "' (" + pair.Value + " view(s))")
            .ToList();

        var more = totalViewCount > views.Count ? " (+" + (totalViewCount - views.Count) + " more)" : string.Empty;
        return lead + " " + views.Count + " view(s) with their OWN filters" + more +
               ": " + string.Join(", ", filters) + ". They are replaced by the template's filter list.";
    }

    internal static string NameOf(Document doc, long filterId) =>
        doc.GetElement(ElementIdCompat.Create(filterId))?.Name ?? "#" + filterId;
}
