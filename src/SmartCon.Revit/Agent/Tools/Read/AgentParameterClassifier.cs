using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Tools.Read;

internal static class AgentParameterClassifier
{
    internal const string System = "system";
    internal const string Shared = "shared";
    internal const string Family = "family";

    internal static string Classify(Parameter parameter)
    {
        if (parameter.IsShared)
            return Shared;

        if (parameter.Definition is InternalDefinition definition
            && definition.BuiltInParameter != BuiltInParameter.INVALID)
        {
            return System;
        }

        return Family;
    }

    internal static bool IsBuiltIn(Parameter parameter, out BuiltInParameter builtInParameter)
    {
        builtInParameter = BuiltInParameter.INVALID;
        if (parameter.Definition is not InternalDefinition definition)
            return false;

        builtInParameter = definition.BuiltInParameter;
        return builtInParameter != BuiltInParameter.INVALID;
    }

    internal static bool IsMeaningful(Parameter? parameter)
    {
        if (parameter is null || !parameter.HasValue)
            return false;

        try
        {
            return parameter.StorageType switch
            {
                StorageType.String => !string.IsNullOrWhiteSpace(parameter.AsString()),
                StorageType.Integer => parameter.AsInteger() != 0,
                StorageType.Double => Math.Abs(parameter.AsDouble()) > 1e-9,
                StorageType.ElementId => parameter.AsElementId() != ElementId.InvalidElementId,
                _ => true
            };
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string? ReadOnlyReason(Parameter parameter, Element? owner)
    {
        if (!parameter.IsReadOnly)
            return null;

        if (owner is not null && !owner.IsModifiable)
            return "element_not_modifiable";

        return "not_writable";
    }
}