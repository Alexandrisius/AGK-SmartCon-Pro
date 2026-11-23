using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Query;

/// <summary>
/// Post-query hint for an empty result whose condition names a parameter that
/// lives on element TYPES in this project (a type binding or a type-only
/// built-in / family parameter). The query reads such a parameter from each
/// element's type, so an empty result means no type value matched — the honest
/// answer says where the value lives and points at the type-level tools
/// (live case 9, C9-03). The hint only speaks for values the query itself
/// evaluated: for a query without includeTypeParameters a parameter reachable
/// only through the by-name type lookup stays unhinted.
/// </summary>
internal static class AgentQueryTypeHint
{
    private const int MaxTypesToScan = 500;

    internal sealed class TypeBoundHint
    {
        public string Parameter = string.Empty;

        public int TypeCount;

        public string Message = string.Empty;
    }

    /// <summary>
    /// Hint for the first condition parameter that is type-bound in this project,
    /// or null when nothing useful can be said (no conditions, the parameter is
    /// instance-bound, or no type of the requested categories carries it).
    /// </summary>
    internal static TypeBoundHint? Describe(
        Document doc,
        QuerySpec spec,
        AgentQueryResult result)
    {
        if (result.Total != 0 || result.FirstCandidateId is not { } candidateId)
            return null;

        var candidate = doc.GetElement(ElementIdCompat.Create(candidateId));
        if (candidate is null)
            return null;

        var categoryIds = AgentCategoryResolver.Resolve(doc, spec.Categories, new List<string>());
        foreach (var condition in result.Conditions)
        {
            if (condition.Field.Length > 0 || condition.Parameter.Length == 0)
                continue;

            var parameter = AgentParameterResolver.Find(
                doc, candidate, condition.Parameter, includeTypeParameters: true);
            if (parameter is null || !IsTypeBound(doc, candidate, parameter))
                continue;

            // The hint may only describe what the query itself evaluated. A
            // parameter reachable ONLY through the by-name type lookup was
            // invisible to a flagless query; claiming type-level facts there
            // would be a guess, not an explanation.
            if (!spec.IncludeTypeParameters)
            {
                var flagless = AgentParameterResolver.Find(
                    doc, candidate, condition.Parameter, includeTypeParameters: false);
                if (flagless is null || flagless.Id != parameter.Id)
                    continue;
            }

            var typeCount = CountTypesCarryingParameter(doc, categoryIds, condition.Parameter);
            if (typeCount == 0)
                continue;

            return new TypeBoundHint
            {
                Parameter = condition.Parameter,
                TypeCount = typeCount,
                Message =
                    "Parameter '" + condition.Parameter + "' is bound to element types (" +
                    typeCount + " type(s) carry it): the value lives on the element type and " +
                    "none of the scanned elements matched — use find_families_with_parameter / " +
                    "list_family_types to see the type-level values."
            };
        }

        return null;
    }

    private static bool IsTypeBound(Document doc, Element element, Parameter parameter)
    {
        try
        {
            // A resolved parameter whose owner is the element's TYPE: the value
            // lives on the type, the instance view carries nothing of its own.
            var owner = parameter.Element;
            if (owner is not null && owner.Id.GetValue() != element.Id.GetValue()
                && owner is ElementType)
            {
                return true;
            }

            // Project / shared parameters: the binding class says where the value
            // lives (TypeBinding vs InstanceBinding), matched by definition name.
            if (parameter.Definition?.Name is { Length: > 0 } name
                && TryFindBinding(doc, name, out var binding))
            {
                return binding is TypeBinding;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    private static bool TryFindBinding(Document doc, string name, out ElementBinding? binding)
    {
        binding = null;
        var map = doc.ParameterBindings;
        var iterator = map.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Current is not ElementBinding current)
                continue;

            var definitionName = (iterator.Key as Definition)?.Name;
            if (string.Equals(definitionName, name, StringComparison.OrdinalIgnoreCase))
            {
                binding = current;
                return true;
            }
        }

        return false;
    }

    private static int CountTypesCarryingParameter(
        Document doc,
        IReadOnlyList<ElementId> categoryIds,
        string parameterName)
    {
        if (categoryIds.Count == 0)
            return 0;

        var collector = new FilteredElementCollector(doc)
            .WhereElementIsElementType()
            .WherePasses(new ElementMulticategoryFilter(categoryIds.ToList()));

        var count = 0;
        foreach (Element type in collector)
        {
            if (AgentParameterResolver.Find(doc, type, parameterName, false) is not null)
                count++;

            if (count >= MaxTypesToScan)
                break;
        }

        return count;
    }
}
