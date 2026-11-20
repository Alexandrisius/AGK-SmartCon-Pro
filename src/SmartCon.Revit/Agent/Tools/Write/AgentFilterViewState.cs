using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Revit UI has two independent checkboxes for an applied view filter (#400):
/// whether the filter itself is enabled, and «Видимость» of the elements that
/// match it. <c>none</c> used to leave the filter enabled, so it behaved like
/// <c>show</c>; this helper is the single place that maps the tool argument to
/// both flags.
/// </summary>
internal enum ViewFilterMode
{
    /// <summary>Filter stays on the view but is disabled: elements are visible again.</summary>
    None,

    /// <summary>Filter enabled and matching elements are hidden.</summary>
    Hide,

    /// <summary>Filter enabled and matching elements stay visible (overrides only).</summary>
    Show
}

internal static class AgentFilterViewState
{
    internal static bool TryParse(string? value, out ViewFilterMode mode)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "none":
                mode = ViewFilterMode.None;
                return true;
            case "hide":
                mode = ViewFilterMode.Hide;
                return true;
            case "show":
                mode = ViewFilterMode.Show;
                return true;
            default:
                mode = ViewFilterMode.None;
                return false;
        }
    }

    internal static string ToWireName(ViewFilterMode mode) =>
        mode switch
        {
            ViewFilterMode.Hide => "hide",
            ViewFilterMode.Show => "show",
            _ => "none"
        };

    /// <summary>
    /// Applies the mode: hide = enabled + «Видимость» off; show = enabled +
    /// visible; none = filter disabled (visibility may stay on).
    /// </summary>
    internal static void Apply(View view, ElementId filterId, ViewFilterMode mode)
    {
        switch (mode)
        {
            case ViewFilterMode.Hide:
                view.SetIsFilterEnabled(filterId, true);
                view.SetFilterVisibility(filterId, false);
                break;
            case ViewFilterMode.Show:
                view.SetIsFilterEnabled(filterId, true);
                view.SetFilterVisibility(filterId, true);
                break;
            default:
                view.SetIsFilterEnabled(filterId, false);
                break;
        }
    }

    internal static bool ReadEnabled(View view, ElementId filterId)
    {
        try
        {
            return view.GetIsFilterEnabled(filterId);
        }
        catch (Exception)
        {
            return true;
        }
    }

    internal static bool ReadVisible(View view, ElementId filterId)
    {
        try
        {
            return view.GetFilterVisibility(filterId);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Human-readable state: filterEnabled + visible + the mode it means.</summary>
    internal static string Describe(View view, ElementId filterId)
    {
        var enabled = ReadEnabled(view, filterId);
        var visible = ReadVisible(view, filterId);
        return "filterEnabled=" + (enabled ? "true" : "false") +
               ", visible=" + (visible ? "true" : "false") +
               ", mode=" + ModeName(enabled, visible);
    }

    internal static string ModeName(bool enabled, bool visible) =>
        !enabled ? "none" : visible ? "show" : "hide";

    internal static ElementId AsId(long filterId) => ElementIdCompat.Create(filterId);
}