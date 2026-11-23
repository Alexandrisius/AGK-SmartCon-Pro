using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Tools.Write;

internal static partial class AgentParameterWriteHelper
{
    // A refusal of the VALUE looks exactly like a type that has no such field
    // unless the two are told apart: only the second one is final.
    private static bool NotSupported(
        out string errorCode,
        out string reason,
        Parameter parameter)
    {
        if (LivesOnTheElementType(parameter, out var name))
        {
            errorCode = "scope_mismatch";
            reason = "Parameter '" + name + "' belongs to the element type. Pass scope=\"type\" " +
                     "to change it (it affects every instance of the type).";
            return false;
        }

        var category = parameter.Element?.Category?.Name;
        errorCode = "parameter_not_supported";
        reason = "Parameter '" + parameter.Definition?.Name + "' cannot be written on this " +
                 "element" + (string.IsNullOrEmpty(category) ? string.Empty : " (category '" + category + "')") +
                 ": Revit exposes no such value on this kind of element. Skip it and tell the " +
                 "user that these elements do not carry this parameter.";
        return false;
    }

    // An instance shows a type-bound built-in (the Assembly Code, for one) as a
    // value it does not own: the write belongs to the type.
    private static bool LivesOnTheElementType(Parameter parameter, out string name)
    {
        name = parameter.Definition?.Name ?? string.Empty;
        var element = parameter.Element;
        if (element is null
            || element is ElementType
            || element.Document.GetElement(element.GetTypeId()) is not ElementType ownerType
            || parameter.Definition is not InternalDefinition internalDefinition
            || internalDefinition.BuiltInParameter == BuiltInParameter.INVALID)
        {
            return false;
        }

        return ownerType.get_Parameter(internalDefinition.BuiltInParameter) is not null;
    }
}
