using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Detail level and display style are the "why is my 3D view schematic?"
/// half of view creation: pipes, ducts and conduits are single lines at
/// Coarse/Medium, and a newly created 3D view is Medium unless asked otherwise.
/// </summary>
internal static class AgentViewDetailHelper
{
    internal static bool TryParse(string? value, out ViewDetailLevel level)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "coarse":
                level = ViewDetailLevel.Coarse;
                return true;
            case "medium":
                level = ViewDetailLevel.Medium;
                return true;
            case "fine":
                level = ViewDetailLevel.Fine;
                return true;
            default:
                level = ViewDetailLevel.Undefined;
                return false;
        }
    }

    internal static bool Apply(View view, string? requested, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return true;

        if (!TryParse(requested, out var level))
        {
            warnings.Add("detailLevel must be one of: coarse, medium, fine — '" +
                         requested + "' was ignored for view '" + view.Name + "'.");
            return false;
        }

        try
        {
            if (!view.CanModifyDetailLevel())
            {
                warnings.Add(
                    "The detail level of '" + view.Name + "' cannot be changed (view type " +
                    view.ViewType + "). [Action: change it in the Revit UI]");
                return false;
            }

            view.DetailLevel = level;
            if (view.DetailLevel != level)
            {
                warnings.Add(
                    "The detail level of '" + view.Name + "' stays " + view.DetailLevel +
                    " (requested " + level + "): the view template controls VIEW_DETAIL_LEVEL. " +
                    "[Action: change the template or detach it with " +
                    "set_view_template_inclusion(parameters=[\"detailLevel\"], included=false)]");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            warnings.Add(
                "The detail level of '" + view.Name + "' could not be set: " + ex.Message +
                ". [Action: check the view template]");
            return false;
        }
    }

    /// <summary>
    /// A 3D view at Coarse/Medium draws MEP curves as single lines. The owner
    /// lost a round on exactly this: the view looked schematic and nothing in
    /// the answer said why. Warn whenever a 3D view is not Fine, with the fix.
    /// </summary>
    internal static void WarnIfMepLooksSchematic(View view, List<string> warnings)
    {
        if (view.ViewType != ViewType.ThreeD)
            return;

        var level = view.DetailLevel;
        if (level == ViewDetailLevel.Fine)
            return;

        warnings.Add(
            "3D view '" + view.Name + "' uses " + level + " detail: at Coarse/Medium pipes, " +
            "ducts and conduits are drawn as single lines, so the view cannot show a " +
            "schematic system with fittings. [Action: create the view with " +
            "detailLevel=\"fine\" or set VIEW_DETAIL_LEVEL on the view/template]");
    }
}