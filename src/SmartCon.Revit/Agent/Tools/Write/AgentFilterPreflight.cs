using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// One parameter used by a filter rule that Revit does not accept for some of
/// the target categories.
/// </summary>
internal sealed class UnsupportedFilterParameter
{
    public long ParameterId { get; set; }

    public string? ParameterName { get; set; }

    public List<(long Id, string? Name)> UnsupportedCategories { get; } = new();
}

/// <summary>
/// Pre-flight for the parameter × category pair (#400): a filter rule is only
/// accepted when every category supports the parameter, so the tool checks
/// <see cref="ParameterFilterUtilities.GetFilterableParametersInCommon"/>
/// before it touches the model.
/// </summary>
internal static class AgentFilterPreflight
{
    internal static ICollection<ElementId> FilterableInCommon(
        Document doc,
        IReadOnlyList<ElementId> categories)
    {
        try
        {
            return ParameterFilterUtilities.GetFilterableParametersInCommon(
                doc, new List<ElementId>(categories));
        }
        catch (Exception)
        {
            return Array.Empty<ElementId>();
        }
    }

    /// <summary>
    /// Parameters of the rule tree that at least one target category does not
    /// support. Empty means the pair passed the pre-flight.
    /// </summary>
    internal static List<UnsupportedFilterParameter> FindUnsupported(
        Document doc,
        IReadOnlyList<ElementId> categories,
        ElementFilter? filter)
    {
        var result = new List<UnsupportedFilterParameter>();
        if (filter is null || categories.Count == 0)
            return result;

        var perCategory = new List<(ElementId Id, HashSet<long> Supported)>();
        foreach (var categoryId in categories)
        {
            var supported = new HashSet<long>();
            foreach (var id in FilterableInCommon(doc, new[] { categoryId }))
                supported.Add(id.GetValue());
            perCategory.Add((categoryId, supported));
        }

        foreach (var parameterId in CollectParameterIds(filter))
        {
            var unsupported = new List<(long Id, string? Name)>();
            foreach (var pair in perCategory)
            {
                if (pair.Supported.Contains(parameterId.GetValue()))
                    continue;

                unsupported.Add((
                    pair.Id.GetValue(),
                    Category.GetCategory(doc, pair.Id)?.Name));
            }

            if (unsupported.Count == 0)
                continue;

            result.Add(new UnsupportedFilterParameter
            {
                ParameterId = parameterId.GetValue(),
                ParameterName = DescribeParameter(doc, parameterId)
            });
            result[result.Count - 1].UnsupportedCategories.AddRange(unsupported);
        }

        return result;
    }

    internal static List<ElementId> CollectParameterIds(ElementFilter? filter)
    {
        var ids = new List<ElementId>();
        var seen = new HashSet<long>();
        Collect(filter, ids, seen);
        return ids;
    }

    private static void Collect(
        ElementFilter? filter,
        List<ElementId> ids,
        HashSet<long> seen)
    {
        if (filter is null)
            return;

        try
        {
            if (filter is ElementLogicalFilter logical)
            {
                foreach (var child in logical.GetFilters())
                    Collect(child, ids, seen);
                return;
            }

            if (filter is ElementParameterFilter parameterFilter)
            {
                foreach (var rule in parameterFilter.GetRules())
                {
                    var id = rule.GetRuleParameter();
                    if (id is null || id == ElementId.InvalidElementId)
                        continue;

                    if (seen.Add(id.GetValue()))
                        ids.Add(id);
                }
            }
        }
        catch (Exception)
        {
            // A rule kind the API cannot enumerate is skipped: the real dry run
            // still validates the filter inside the rolled-back transaction.
        }
    }

    /// <summary>Localized parameter name exactly as the Revit UI shows it.</summary>
    internal static string? DescribeParameter(Document doc, ElementId parameterId)
    {
        var value = parameterId.GetValue();
        if (value < 0)
        {
            try
            {
                var label = LabelUtils.GetLabelFor((BuiltInParameter)value);
                if (!string.IsNullOrWhiteSpace(label))
                    return label;
            }
            catch (Exception)
            {
                // Some built-in parameters have no label.
            }

            return ((BuiltInParameter)value).ToString();
        }

        try
        {
            var element = doc.GetElement(ElementIdCompat.Create(value));
            if (element is not null && !string.IsNullOrWhiteSpace(element.Name))
                return element.Name;
        }
        catch (Exception)
        {
            // The id does not resolve; fall through to the numeric form.
        }

        return value.ToString();
    }
}