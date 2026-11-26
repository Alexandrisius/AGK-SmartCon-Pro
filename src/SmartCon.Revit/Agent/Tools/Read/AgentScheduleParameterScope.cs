using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>Which signal established the scope of a schedule field's parameter.</summary>
internal readonly struct ScheduleScopeVerdict
{
    internal ScheduleScopeVerdict(string? scope, string? source, bool conflict, string? note)
    {
        Scope = scope;
        Source = source;
        Conflict = conflict;
        Note = note;
    }

    internal string? Scope { get; }

    /// <summary>fieldType | binding | elements | null.</summary>
    internal string? Source { get; }

    /// <summary>The binding map and the scheduled elements disagree; the elements won.</summary>
    internal bool Conflict { get; }

    internal string? Note { get; }
}

/// <summary>
/// The level a schedule field's parameter lives on — "instance" or "type" —
/// for the scope= argument of the write tools. ScheduleField.FieldType cannot
/// answer this for shared parameters: the API reference states that "all
/// shared parameters also use [the Instance] type, regardless of whether
/// they are instance or type parameters". The binding map is the usual
/// authority, but it can be ambiguous when a definition is bound both ways;
/// the scheduled elements themselves are the ground truth and win on a
/// disagreement (live feedback 2026-11-25: a type-bound ADSK parameter was
/// reported as instance).
/// </summary>
internal static class AgentScheduleParameterScope
{
    internal const string Instance = "instance";

    internal const string Type = "type";

    /// <summary>Elements per definition that may confirm the level of a family parameter.</summary>
    private const int MaxProbedElements = 40;

    /// <summary>Elements pre-collected by the caller, shared by all fields.</summary>
    private const int MaxSourceElements = 400;

    internal static ScheduleScopeVerdict Describe(
        Document doc,
        ScheduleDefinition definition,
        ScheduleField field,
        IReadOnlyList<Element>? sourceElements = null)
    {
        try
        {
            var parameterId = field.ParameterId;
            if (parameterId is null || parameterId == ElementId.InvalidElementId)
                return Empty();

            var fieldType = field.FieldType;
            if (fieldType == ScheduleFieldType.ElementType)
                return new ScheduleScopeVerdict(Type, "fieldType", false, null);

            if (fieldType != ScheduleFieldType.Instance)
                return Empty();

            var value = parameterId.GetValue();
            if (value < 0)
                return new ScheduleScopeVerdict(Instance, "fieldType", false, null);

            if (doc.GetElement(parameterId) is not ParameterElement parameterElement)
                return Empty();

            var parameterDefinition = parameterElement.GetDefinition();
            if (parameterDefinition is null)
                return Empty();

            var binding = DescribeByBindings(doc, parameterElement, parameterDefinition);
            var elements = DescribeByElements(
                doc,
                parameterDefinition,
                parameterElement,
                sourceElements ?? CollectSource(doc, definition));

            if (elements is not null)
            {
                if (binding is not null && binding != elements)
                {
                    return new ScheduleScopeVerdict(
                        elements,
                        "elements",
                        true,
                        "The project binding says " + binding + " but the scheduled elements " +
                        "carry the parameter as " + elements + "; the element verdict is used.");
                }

                return new ScheduleScopeVerdict(elements, "elements", false, null);
            }

            if (binding is not null)
                return new ScheduleScopeVerdict(binding, "binding", false, null);

            return Empty();
        }
        catch (Exception)
        {
            return Empty();
        }
    }

    private static ScheduleScopeVerdict Empty() => new(null, null, false, null);

    /// <summary>
    /// Every binding of the definition, not one cached lookup: a shared
    /// parameter may be bound as type in one category and as instance in another,
    /// and get_Item returns only one of them.
    /// </summary>
    private static string? DescribeByBindings(
        Document doc,
        ParameterElement parameterElement,
        Definition definition)
    {
        try
        {
            bool hasType = false;
            bool hasInstance = false;
            var iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();
            while (iterator.MoveNext())
            {
                if (iterator.Key is not Definition candidate || !SameDefinition(candidate, parameterElement))
                    continue;

                if (iterator.Current is TypeBinding)
                    hasType = true;
                else if (iterator.Current is InstanceBinding)
                    hasInstance = true;
            }

            if (hasType && hasInstance)
                return null;
            if (hasType)
                return Type;
            if (hasInstance)
                return Instance;

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SameDefinition(Definition candidate, ParameterElement parameterElement)
    {
        if (candidate is InternalDefinition internalCandidate
            && parameterElement.GetDefinition() is InternalDefinition internalTarget
            && internalCandidate.Id == internalTarget.Id)
        {
            return true;
        }

        return string.Equals(candidate.Name, parameterElement.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// A family parameter that is not bound in the project: the level is asked of
    /// the scheduled elements. A type parameter read through an instance belongs to
    /// the instance's ElementType (Parameter.Element, the API-supported signal),
    /// so it is type scope even though element.get_Parameter finds it.
    /// </summary>
    private static string? DescribeByElements(
        Document doc,
        Definition definition,
        ParameterElement parameterElement,
        IReadOnlyList<Element> sourceElements)
    {
        var guid = (parameterElement as SharedParameterElement)?.GuidValue;
        string? verdict = null;
        var probed = 0;
        foreach (var element in sourceElements)
        {
            var level = LevelOf(doc, element, guid, definition);
            if (level is null)
                continue;

            if (verdict is null)
                verdict = level;
            else if (verdict != level)
                return null;

            if (++probed >= MaxProbedElements)
                break;
        }

        return verdict;
    }

    private static string? LevelOf(
        Document doc,
        Element element,
        Guid? guid,
        Definition definition)
    {
        try
        {
            var parameter = ParameterOf(element, guid, definition);
            if (parameter is not null)
            {
                if (parameter.Element is ElementType)
                    return Type;
                if (element.Parameters.Contains(parameter))
                    return Instance;
            }

            var typeId = element.GetTypeId();
            if (typeId is null || typeId == ElementId.InvalidElementId)
                return null;

            var type = doc.GetElement(typeId);
            if (type is null)
                return null;

            var typeParameter = ParameterOf(type, guid, definition);
            if (typeParameter is null)
                return null;

            return typeParameter.Element is ElementType || type.Parameters.Contains(typeParameter)
                ? Type
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Parameter? ParameterOf(Element element, Guid? guid, Definition definition) =>
        guid is { } value ? element.get_Parameter(value) : element.get_Parameter(definition);

    private static IReadOnlyList<Element> CollectSource(Document doc, ScheduleDefinition definition)
    {
        var elements = new List<Element>();
        try
        {
            foreach (var element in AgentScheduleReader.CollectSourceElements(
                         doc, definition, MaxSourceElements))
            {
                elements.Add(element);
                if (elements.Count >= MaxSourceElements)
                    break;
            }
        }
        catch (Exception)
        {
            // The element probe is optional; binding/fieldType still answer.
        }

        return elements;
    }
}
