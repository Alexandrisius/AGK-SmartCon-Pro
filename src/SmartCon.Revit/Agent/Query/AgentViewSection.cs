using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Query;

/// <summary>
/// Reads the per-view value of the project parameter «Раздел» (bound to the
/// Views category; shared or not — found by name, never a BuiltInParameter).
/// The section is editable data on the view: list_views exposes it as the
/// `section` card field and the select filter of write tools matches it.
/// </summary>
internal static class AgentViewSection
{
    internal const string ParameterName = "Раздел";

    /// <summary>
    /// The trimmed parameter value, or null when the view has no «Раздел»
    /// parameter or its value is empty.
    /// </summary>
    internal static string? ReadValue(View view) =>
        TryRead(view, out var value) ? value : null;

    /// <summary>
    /// True when the view has the «Раздел» parameter at all (even with an
    /// empty value); <paramref name="value"/> is null for a missing parameter
    /// or an empty value.
    /// </summary>
    internal static bool TryRead(View view, out string? value)
    {
        value = null;
        var parameter = FindParameter(view);
        if (parameter is null)
            return false;

        if (parameter.HasValue)
        {
            var raw = parameter.AsString() ?? parameter.AsValueString();
            if (!string.IsNullOrWhiteSpace(raw))
                value = raw.Trim();
        }

        return true;
    }

    private static Parameter? FindParameter(View view)
    {
        try
        {
            foreach (Parameter parameter in view.Parameters)
            {
                if (string.Equals(
                        parameter.Definition?.Name,
                        ParameterName,
                        StringComparison.OrdinalIgnoreCase))
                    return parameter;
            }
        }
        catch (Exception)
        {
            // A broken definition must not break listing or selecting views.
        }

        return null;
    }
}
