using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

internal sealed class AgentParameterFilter
{
    public HashSet<string> Classes { get; set; } = new(StringComparer.Ordinal);

    public HashSet<string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<BuiltInParameter>? _nameBuiltIns;

    /// <summary>
    /// The built-in ids <see cref="Names"/> stand for besides the display
    /// names (enum ids and aliases). Built on first use: Names is filled by
    /// the object initializer before the filter is applied.
    /// </summary>
    public HashSet<BuiltInParameter> NameBuiltIns =>
        _nameBuiltIns ??= AgentParameterResolver.BuiltInsOf(
            Names.Where(name => !string.IsNullOrEmpty(name))!);

    public string? NameContains { get; set; }

    public bool OnlyNonEmpty { get; set; }

    public bool ExcludeDerived { get; set; }

    public int Max { get; set; } = 50;

    public IReadOnlyCollection<BuiltInParameter> EssenceProfile { get; set; } =
        Array.Empty<BuiltInParameter>();

    /// <summary>
    /// Copy with a new parameter cap and essence profile: get_element_details
    /// rebuilds its payload with shrinking caps until the batch fits the
    /// context budget, and every rebuild must not disturb the template.
    /// </summary>
    public AgentParameterFilter Clone(int max, Category? category) => new()
    {
        Classes = Classes,
        Names = Names,
        NameContains = NameContains,
        OnlyNonEmpty = OnlyNonEmpty,
        ExcludeDerived = ExcludeDerived,
        Max = max,
        EssenceProfile = AgentElementEssence.ProfileFor(category)
    };
}

internal static class AgentParameterCollector
{
    internal static IReadOnlyList<ParameterValueInfo> Collect(
        Document doc,
        Element owner,
        ParameterSet parameters,
        string scope,
        AgentParameterFilter filter,
        out int total,
        out bool truncated,
        out int familyOmitted)
    {
        total = parameters.Size;
        familyOmitted = 0;
        var eligible = new List<Parameter>(parameters.Size);

        foreach (Parameter parameter in parameters)
        {
            if (!PassesNameFilters(parameter, filter))
                continue;

            var parameterClass = AgentParameterClassifier.Classify(parameter);
            if (!filter.Classes.Contains(parameterClass))
            {
                if (string.Equals(
                        parameterClass,
                        AgentParameterClassifier.Family,
                        StringComparison.Ordinal))
                {
                    familyOmitted++;
                }

                continue;
            }

            if (filter.ExcludeDerived && parameter.IsReadOnly)
                continue;

            if (filter.OnlyNonEmpty && !AgentParameterClassifier.IsMeaningful(parameter))
                continue;

            eligible.Add(parameter);
        }

        eligible.Sort((left, right) => Compare(left, right, filter.EssenceProfile));

        var result = new List<ParameterValueInfo>(Math.Min(filter.Max, eligible.Count));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in eligible)
        {
            if (result.Count >= filter.Max)
                break;

            var info = AgentParameterValueReader.Create(doc, parameter, scope);
            var key = info.Name + "|" + info.Scope + "|" + info.StorageType + "|" + info.Value;
            if (seen.Add(key))
                result.Add(info);
        }

        truncated = eligible.Count > filter.Max;
        return result;
    }

    internal static int CountFamilyOmitted(ParameterSet parameters, AgentParameterFilter filter)
{
        if (filter.Classes.Contains(AgentParameterClassifier.Family))
            return 0;

        var count = 0;
        foreach (Parameter parameter in parameters)
        {
            if (!PassesNameFilters(parameter, filter))
                continue;

            if (string.Equals(
                    AgentParameterClassifier.Classify(parameter),
                    AgentParameterClassifier.Family,
                    StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static bool PassesNameFilters(Parameter parameter, AgentParameterFilter filter)
    {
        var name = parameter.Definition?.Name;
        if (filter.Names.Count > 0
            && (string.IsNullOrEmpty(name) || !filter.Names.Contains(name!))
            && !(AgentParameterClassifier.IsBuiltIn(parameter, out var builtIn)
                 && filter.NameBuiltIns.Contains(builtIn)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.NameContains)
            && (string.IsNullOrEmpty(name)
                || name!.IndexOf(filter.NameContains!, StringComparison.OrdinalIgnoreCase) < 0))
        {
            return false;
        }

        return true;
    }

    private static int Compare(
        Parameter left,
        Parameter right,
        IReadOnlyCollection<BuiltInParameter> essenceProfile)
    {
        var rank = Rank(left, essenceProfile).CompareTo(Rank(right, essenceProfile));
        if (rank != 0)
            return rank;

        return string.Compare(
            left.Definition?.Name ?? string.Empty,
            right.Definition?.Name ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(
        Parameter parameter,
        IReadOnlyCollection<BuiltInParameter> essenceProfile)
    {
        if (AgentParameterClassifier.IsBuiltIn(parameter, out var builtInParameter)
            && essenceProfile.Contains(builtInParameter))
        {
            return 0;
        }

        var parameterClass = AgentParameterClassifier.Classify(parameter);
        if (string.Equals(parameterClass, AgentParameterClassifier.System, StringComparison.Ordinal))
            return 1;

        if (string.Equals(parameterClass, AgentParameterClassifier.Shared, StringComparison.Ordinal))
            return 2;

        return 3;
    }
}