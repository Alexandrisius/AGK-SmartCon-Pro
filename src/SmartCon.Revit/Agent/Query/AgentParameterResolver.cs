using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Query;

internal static class AgentParameterResolver
{
    internal static Parameter? Find(
        Document doc,
        Element element,
        string name,
        bool includeTypeParameters)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

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