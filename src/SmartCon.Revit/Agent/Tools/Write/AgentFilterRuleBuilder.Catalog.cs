using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Resolves filterable parameters for a category set and exposes them by the
/// localized name (what the user types) and by the BuiltInParameter enum name.
/// </summary>
internal sealed class FilterParameterCatalog
{
    private readonly Dictionary<string, ElementId> _byName;
    private readonly HashSet<string> _existing;

    private FilterParameterCatalog(
        Dictionary<string, ElementId> byName,
        IReadOnlyList<string> names,
        HashSet<string> existing)
    {
        _byName = byName;
        Names = names;
        _existing = existing;
    }

    public IReadOnlyList<string> Names { get; }

    public static FilterParameterCatalog Build(
        Document doc,
        IEnumerable<ElementId> categories)
    {
        var byName = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var existing = BuildExistingParameterNames(doc);

        ICollection<ElementId> filterable;
        try
        {
            filterable = ParameterFilterUtilities.GetFilterableParametersInCommon(
                doc, new List<ElementId>(categories));
        }
        catch (Exception)
        {
            return new FilterParameterCatalog(byName, names, existing);
        }

        foreach (var id in filterable)
        {
            var value = id.GetValue();
            var labels = new List<string>(2);
            if (value < 0)
            {
                var builtIn = (BuiltInParameter)value;
                labels.Add(builtIn.ToString());
                try
                {
                    var localized = LabelUtils.GetLabelFor(builtIn);
                    if (!string.IsNullOrWhiteSpace(localized))
                        labels.Add(localized);
                }
                catch (Exception)
                {
                    // Some built-in parameters have no label.
                }
            }
            else
            {
                var element = doc.GetElement(ElementIdCompat.Create(value));
                if (element is not null && !string.IsNullOrWhiteSpace(element.Name))
                    labels.Add(element.Name);
            }

            foreach (var label in labels)
            {
                var key = Normalize(label);
                if (key.Length == 0 || byName.ContainsKey(key))
                    continue;
                byName.Add(key, id);
                names.Add(label);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return new FilterParameterCatalog(byName, names, existing);
    }

    private static HashSet<string> BuildExistingParameterNames(Document doc)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var element in new FilteredElementCollector(doc)
                         .OfClass(typeof(ParameterElement)))
            {
                if (element is ParameterElement parameter && !string.IsNullOrWhiteSpace(parameter.Name))
                    names.Add(Normalize(parameter.Name));
            }
        }
        catch (Exception)
        {
            // The existence index is a diagnostic aid; never fail the build on it.
        }

        return names;
    }

    /// <summary>
    /// True when a project/shared parameter with this name exists, even if Revit
    /// does not allow filtering the requested categories by it. Distinguishes
    /// parameter_not_filterable from a genuinely unknown parameter.
    /// </summary>
    public bool ExistsInDocument(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return _existing.Contains(Normalize(name!));
    }

    public bool TryResolve(string? name, out ElementId id)
    {
        id = ElementId.InvalidElementId;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (_byName.TryGetValue(Normalize(name!), out var found) && found is not null)
        {
            id = found;
            return true;
        }

        return false;
    }

    public bool Contains(ElementId id)
    {
        foreach (var candidate in _byName.Values)
        {
            if (candidate.GetValue() == id.GetValue())
                return true;
        }

        return false;
    }

    public IReadOnlyList<string> Sample(int max)
    {
        if (Names.Count <= max)
            return Names;

        var sample = new List<string>(max);
        for (var index = 0; index < max; index++)
            sample.Add(Names[index]);
        return sample;
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace('ё', 'е').Replace("  ", " ");
}

internal sealed class FilterRuleBuildResult
{
    public ElementFilter? Filter { get; init; }

    public string? ErrorCode { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<string> AvailableParameters { get; init; } = Array.Empty<string>();

    public bool Ok => ErrorCode is null;
}