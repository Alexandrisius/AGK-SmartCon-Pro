using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// The affected-views context of set_view_template_inclusion: the change lives
/// on the template, so the answer names the views that use it.
/// </summary>
public sealed partial class SetViewTemplateInclusionTool
{
    private const int MaxReportedAffectedViews = 50;

    private static void AddContext(
        Document doc,
        WriteOutcome outcome,
        View template,
        IReadOnlyList<object> reports)
    {
        var affected = new List<object>();
        var count = 0;
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(View)))
        {
            if (element is not View view || view.IsTemplate)
                continue;

            var assigned = view.ViewTemplateId;
            if (assigned is null || assigned != template.Id)
                continue;

            count++;
            if (affected.Count < MaxReportedAffectedViews)
            {
                affected.Add(new
                {
                    viewId = view.Id.GetValue(),
                    name = view.Name,
                    viewType = view.ViewType.ToString()
                });
            }
        }

        outcome.Data["templateId"] = template.Id.GetValue();
        outcome.Data["templateName"] = template.Name;
        outcome.Data["affectedViewCount"] = count;
        outcome.Data["affectedViews"] = affected;
        outcome.Data["parameters"] = reports;
        outcome.Data["hint"] = count == 0
            ? "No view currently uses this template; the inclusion change will apply to views " +
              "that use it later."
            : "The change belongs to the template: all " + count +
              " view(s) that use it are affected.";
    }
}
