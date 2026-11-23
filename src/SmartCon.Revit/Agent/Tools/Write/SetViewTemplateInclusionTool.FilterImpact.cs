using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// set_view_template_inclusion and own view filters (C4-01): turning "V/G
/// Overrides Filters" on makes the template the owner of the filter list of
/// every view that uses it. The views' own filters are read before the write
/// and named in the result — afterwards nothing in the model remembers them.
/// </summary>
public sealed partial class SetViewTemplateInclusionTool
{
    private const int MaxReportedViews = 50;

    private static void ReportFilterTakeover(
        WriteOutcome outcome,
        View template,
        IReadOnlyList<(long ViewId, string ViewName, List<string> Filters)> losingViews)
    {
        if (losingViews.Count == 0)
            return;

        var templateId = template.Id.GetValue();
        outcome.Warnings.Add(
            "Including 'V/G Overrides Filters' in template '" + template.Name + "' replaces " +
            "the own filters of the views that use it. " +
            AgentTemplateFilterImpact.DescribeLostFilters("Lost", losingViews, losingViews.Count) +
            " " + AgentTemplateFilterImpact.OrderHint(templateId));
        outcome.Data["ownFiltersReplaced"] = losingViews
            .Take(MaxReportedViews)
            .Select(entry => new
            {
                viewId = entry.ViewId,
                viewName = entry.ViewName,
                templateId,
                filters = entry.Filters
            })
            .ToList();
    }
}
