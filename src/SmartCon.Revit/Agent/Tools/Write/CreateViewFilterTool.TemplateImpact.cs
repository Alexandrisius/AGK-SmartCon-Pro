using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// create_view_filter against a view template (C4-01): a template that includes
/// "V/G Overrides Filters" hands the filter list of every view that uses it to
/// the template as soon as the template changes, so the own filters of those
/// views are dropped. They are read before the write and named afterwards.
/// </summary>
public sealed partial class CreateViewFilterTool
{
    private sealed class TemplateImpactReport
    {
        private const int MaxReportedViews = 50;

        private readonly Dictionary<long, View> _targets = new();
        private readonly Dictionary<long, List<(long ViewId, string ViewName, List<string> Filters)>> _losing = new();

        /// <summary>Template ids whose filter list this call really changed.</summary>
        public HashSet<long> Changed { get; } = new();

        public static TemplateImpactReport Collect(Document doc, IEnumerable<long> eligibleViewIds)
        {
            var report = new TemplateImpactReport();
            foreach (var targetId in eligibleViewIds)
            {
                if (doc.GetElement(ElementIdCompat.Create(targetId)) is View target
                    && target.IsTemplate
                    && AgentTemplateFilterImpact.ControlsFilters(target))
                {
                    report._targets[targetId] = target;
                    report._losing[targetId] = AgentTemplateFilterImpact.ViewsLosingOwnFilters(doc, target);
                }
            }

            return report;
        }

        public void Publish(WriteOutcome outcome)
        {
            // One accumulated list for every changed template: assigning inside
            // the loop kept only the last template's views in the data (the
            // warnings covered all of them) — review F1-R1.
            var replaced = new List<object>();
            foreach (var templateId in Changed.OrderBy(id => id))
            {
                if (!_targets.TryGetValue(templateId, out var template))
                    continue;

                var losingViews = _losing.TryGetValue(templateId, out var views)
                    ? views
                    : new List<(long ViewId, string ViewName, List<string> Filters)>();
                var lost = AgentTemplateFilterImpact.DescribeLostFilters(" Lost", losingViews, losingViews.Count);
                outcome.Warnings.Add(
                    "The filter list of view template '" + template.Name +
                    "' changed. Revit hands the filter list of every view that uses the template " +
                    "to it as soon as the template changes, so own view filters are dropped." +
                    (lost is null ? string.Empty : lost) + " " +
                    AgentTemplateFilterImpact.OrderHint(templateId));
                if (losingViews.Count == 0)
                    continue;

                replaced.AddRange(losingViews
                    .Take(MaxReportedViews)
                    .Select(entry => new
                    {
                        viewId = entry.ViewId,
                        viewName = entry.ViewName,
                        templateId,
                        filters = entry.Filters
                    }));
            }

            if (replaced.Count > 0)
                outcome.Data["ownFiltersReplaced"] = replaced;
        }
    }
}
