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

    /// <summary>
    /// Built-in Assembly Code: renamed from UNIFORMAT_CODE to ASSEMBLY_CODE in the Revit 2026 API.
    /// </summary>
    internal static readonly BuiltInParameter AssemblyCodeBuiltIn =
#if REVIT2026_OR_GREATER
        BuiltInParameter.ASSEMBLY_CODE;
#else
        BuiltInParameter.UNIFORMAT_CODE;
#endif

    /// <summary>
    /// Localized (RU) display names of built-ins, tried only AFTER the plain
    /// name lookups failed: LookupParameter misses some built-in labels
    /// (live case 5 #469: «Код по классификатору» on a type). Late position
    /// keeps project/shared parameters with the same name winning (#469).
    /// </summary>
    private static readonly Dictionary<string, BuiltInParameter> LocalizedBuiltInFallbacks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["код по классификатору"] = AssemblyCodeBuiltIn,
            ["assembly code"] = AssemblyCodeBuiltIn,
            ["униформат"] = AssemblyCodeBuiltIn,
            ["описание"] = BuiltInParameter.ALL_MODEL_DESCRIPTION,
            ["завод-изготовитель"] = BuiltInParameter.ALL_MODEL_MANUFACTURER,
            ["производитель"] = BuiltInParameter.ALL_MODEL_MANUFACTURER,
            ["модель"] = BuiltInParameter.ALL_MODEL_MODEL,
            ["артикул"] = BuiltInParameter.ALL_MODEL_MODEL,
            ["url"] = BuiltInParameter.ALL_MODEL_URL,
            ["примечания к типу"] = BuiltInParameter.ALL_MODEL_TYPE_COMMENTS,
            ["комментарий"] = BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS
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
                return LiftValuelessToType(doc, element, aliased);
        }

        if (Enum.TryParse<BuiltInParameter>(name, ignoreCase: true, out var builtIn))
        {
            var parameter = element.get_Parameter(builtIn);
            if (parameter is not null)
                return LiftValuelessToType(doc, element, parameter);

            var typeParameter = FindOnType(doc, element, type => type.get_Parameter(builtIn));
            if (typeParameter is not null)
                return typeParameter;
        }

        var byName = element.LookupParameter(name);
        if (byName is not null)
            return LiftValuelessToType(doc, element, byName);

        var named = element.GetParameters(name);
        if (named.Count > 0)
            return LiftValuelessToType(doc, element, named[0]);

        if (LocalizedBuiltInFallbacks.TryGetValue(name.Trim(), out var localized))
        {
            var localParameter = element.get_Parameter(localized);
            if (localParameter is not null)
                return LiftValuelessToType(doc, element, localParameter);

            var localTypeParameter = FindOnType(doc, element, type => type.get_Parameter(localized));
            if (localTypeParameter is not null)
                return localTypeParameter;
        }

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

    /// <summary>
    /// Type-bound shared/built-in parameters expose a NON-NULL but valueless
    /// view on instances (live cases 5/8 #468/#469). When the instance view
    /// carries no value and the SAME parameter on the element's type has one,
    /// return the type-side parameter: reads see the real value, writes land
    /// where the parameter actually lives. Genuinely empty both sides keep the
    /// instance view (honest holes stay holes).
    /// </summary>
    private static Parameter? LiftValuelessToType(Document doc, Element element, Parameter candidate)
    {
        if (candidate.HasValue)
            return candidate;

        var typeId = element.GetTypeId();
        if (typeId is null || typeId == ElementId.InvalidElementId)
            return candidate;

        var typeElement = doc.GetElement(typeId);
        if (typeElement is null)
            return candidate;

        Parameter? onType = null;
        try
        {
            if (candidate.IsShared)
                onType = typeElement.get_Parameter(candidate.GUID);
            else if (candidate.Definition is InternalDefinition { BuiltInParameter: { } builtIn })
                onType = typeElement.get_Parameter(builtIn);
        }
        catch (Exception)
        {
            return candidate;
        }

        return onType is not null && onType.HasValue ? onType : candidate;
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