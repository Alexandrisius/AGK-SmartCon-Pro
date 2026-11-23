using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// The level a schedule field's parameter lives on — "instance" or "type" —
/// for the scope= argument of the write tools. ScheduleField.FieldType cannot
/// answer this for shared parameters: the API reference states that "all
/// shared parameters also use [the Instance] type, regardless of whether
/// they are instance or type parameters".
/// </summary>
internal static class AgentScheduleParameterScope
{
    internal const string Instance = "instance";

    internal const string Type = "type";

    /// <summary>Elements pro definition that may confirm the level of a family parameter.</summary>
    private const int MaxProbedElements = 40;

    /// <summary>
    /// "instance" / "type", or null when the level does not apply (a field of
    /// a room, a material, a formula...) or cannot be established: the field
    /// is not a parameter of the scheduled elements, or a family parameter
    /// that is not bound in the project has no element to ask, or is
    /// instance-bound in some families and type-bound in others.
    /// </summary>
    internal static string? Describe(
        Document doc,
        ScheduleDefinition definition,
        ScheduleField field)
    {
        try
        {
            var parameterId = field.ParameterId;
            if (parameterId is null || parameterId == ElementId.InvalidElementId)
                return null;

            var fieldType = field.FieldType;
            if (fieldType == ScheduleFieldType.ElementType)
                return Type;

            if (fieldType != ScheduleFieldType.Instance)
                return null;

            var value = parameterId.GetValue();
            if (value < 0)
                return Instance;

            if (doc.GetElement(parameterId) is not ParameterElement parameterElement)
                return null;

            var parameterDefinition = parameterElement.GetDefinition();
            if (parameterDefinition is null)
                return null;

            // A parameter bound in the project (project parameter or shared
            // parameter added to the project) is instance- or type-bound for
            // the whole project: the binding map is the authority.
            var bound = DescribeByBinding(doc, parameterDefinition);
            if (bound is not null)
                return bound;

            return DescribeByElements(doc, definition, parameterElement, parameterDefinition);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? DescribeByBinding(Document doc, Definition definition)
    {
        try
        {
            // Throws in a family document; null for a definition that is not
            // bound in this project (a parameter defined inside a family).
            return doc.ParameterBindings.get_Item(definition) switch
            {
                InstanceBinding => Instance,
                TypeBinding => Type,
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A family parameter that was never bound in the project: the level is
    /// asked of the elements themselves. An element owns an instance parameter
    /// (it is in Element.Parameters); a type parameter only exposes a valueless
    /// view on the instance and lives on the element's type.
    /// </summary>
    private static string? DescribeByElements(
        Document doc,
        ScheduleDefinition definition,
        ParameterElement parameterElement,
        Definition parameterDefinition)
    {
        var guid = (parameterElement as SharedParameterElement)?.GuidValue;
        string? verdict = null;
        var probed = 0;
        foreach (var element in AgentScheduleReader.CollectSourceElements(
                     doc, definition, AgentScheduleFilterEstimator.MaxScannedElements))
        {
            var level = LevelOf(doc, element, guid, parameterDefinition);
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
            var view = ParameterOf(element, guid, definition);
            if (view is not null && element.Parameters.Contains(view))
                return Instance;

            var typeId = element.GetTypeId();
            if (typeId is null || typeId == ElementId.InvalidElementId)
                return null;

            var type = doc.GetElement(typeId);
            if (type is null)
                return null;

            var onType = ParameterOf(type, guid, definition);
            return onType is not null ? Type : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Parameter? ParameterOf(Element element, Guid? guid, Definition definition) =>
        guid is { } value ? element.get_Parameter(value) : element.get_Parameter(definition);
}
