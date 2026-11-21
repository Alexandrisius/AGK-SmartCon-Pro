using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Query;

internal static class AgentParameterResolver
{
    /// <summary>
    /// Stable English aliases for the built-ins the model asks for most; the
    /// localized display names do not match them (Mark vs Марка), so a plain
    /// LookupParameter refused them in non-English projects (#451).
    /// </summary>
    private static readonly Dictionary<string, BuiltInParameter> BuiltInAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mark"] = BuiltInParameter.ALL_MODEL_MARK,
            ["comments"] = BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
            ["phasecreated"] = BuiltInParameter.PHASE_CREATED,
            ["phase created"] = BuiltInParameter.PHASE_CREATED,
            ["phasedemolished"] = BuiltInParameter.PHASE_DEMOLISHED,
            ["phase demolished"] = BuiltInParameter.PHASE_DEMOLISHED
        };

    internal static Parameter? Find(
        Document doc,
        Element element,
        string name,
        bool includeTypeParameters)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (BuiltInAliases.TryGetValue(name.Trim(), out var alias))
        {
            var aliased = element.get_Parameter(alias);
            if (aliased is not null)
                return aliased;
        }

        if (Enum.TryParse<BuiltInParameter>(name, ignoreCase: true, out var builtIn))
        {
            var parameter = element.get_Parameter(builtIn);
            if (parameter is not null)
                return parameter;

            var typeParameter = FindOnType(doc, element, type => type.get_Parameter(builtIn));
            if (typeParameter is not null)
                return typeParameter;
        }

        var byName = element.LookupParameter(name);
        if (byName is not null)
            return byName;

        var named = element.GetParameters(name);
        if (named.Count > 0)
            return named[0];

        if (!includeTypeParameters)
            return null;

        return FindOnType(doc, element, type =>
        {
            var typeParameter = type.LookupParameter(name);
            if (typeParameter is not null)
                return typeParameter;

            var typeNamed = type.GetParameters(name);
            return typeNamed.Count > 0 ? typeNamed[0] : null;
        });
    }

    private static Parameter? FindOnType(
        Document doc,
        Element element,
        Func<Element, Parameter?> lookup)
    {
        var typeId = element.GetTypeId();
        if (typeId == ElementId.InvalidElementId)
            return null;

        var type = doc.GetElement(typeId);
        return type is null ? null : lookup(type);
    }
}