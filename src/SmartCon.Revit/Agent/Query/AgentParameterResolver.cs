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
    /// Built-in Assembly Description: renamed from UNIFORMAT_DESCRIPTION in the
    /// Revit 2026 API, together with the Assembly Code.
    /// </summary>
    internal static readonly BuiltInParameter AssemblyDescriptionBuiltIn =
#if REVIT2026_OR_GREATER
        BuiltInParameter.ASSEMBLY_DESCRIPTION;
#else
        BuiltInParameter.UNIFORMAT_DESCRIPTION;
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

    /// <summary>
    /// Enum ids of the renamed Assembly Code / Description built-ins, so one
    /// argument resolves on every Revit version. Tried after the enum parse and
    /// the plain name lookups: a project or shared parameter that carries such
    /// a name keeps winning.
    /// </summary>
    private static readonly Dictionary<string, BuiltInParameter> RenamedBuiltInAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["uniformat_code"] = AssemblyCodeBuiltIn,
            ["assembly_code"] = AssemblyCodeBuiltIn,
            ["uniformat_description"] = AssemblyDescriptionBuiltIn,
            ["assembly_description"] = AssemblyDescriptionBuiltIn
        };

    /// <summary>
    /// The built-ins the requested names stand for: an enum id (UNIFORMAT_CODE)
    /// or one of the aliases above. Read tools filter parameters by the same
    /// names the write tools accept - by display name alone a filter with the
    /// enum id returned an empty list that looked like "nothing is written".
    /// </summary>
    internal static HashSet<BuiltInParameter> BuiltInsOf(IEnumerable<string> names)
    {
        var builtIns = new HashSet<BuiltInParameter>();
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var name = raw.Trim();
            if (Enum.TryParse<BuiltInParameter>(name, ignoreCase: true, out var parsed)
                && Enum.IsDefined(typeof(BuiltInParameter), parsed))
            {
                builtIns.Add(parsed);
            }

            if (BuiltInAliases.TryGetValue(name, out var alias))
                builtIns.Add(alias);
            if (LocalizedBuiltInFallbacks.TryGetValue(name, out var localized))
                builtIns.Add(localized);
            if (RenamedBuiltInAliases.TryGetValue(name, out var renamed))
                builtIns.Add(renamed);
        }

        return builtIns;
    }

    /// <summary>
    /// The built-in parameter a NEW parameter's name collides with (C5-01): the
    /// enum id itself, an English alias or a localized (RU) display name. Revit
    /// accepts creating such a parameter, it shadows nothing and read-backs stay
    /// green while the built-in stays empty, so parameter creation tools refuse
    /// the name instead. Null when the name is free of built-in collisions.
    /// </summary>
    internal static BuiltInParameter? BuiltInNameCollision(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        if (Enum.TryParse<BuiltInParameter>(trimmed, ignoreCase: true, out var parsed)
            && Enum.IsDefined(typeof(BuiltInParameter), parsed))
            return parsed;

        if (BuiltInAliases.TryGetValue(trimmed, out var alias))
            return alias;
        if (LocalizedBuiltInFallbacks.TryGetValue(trimmed, out var localized))
            return localized;
        if (RenamedBuiltInAliases.TryGetValue(trimmed, out var renamed))
            return renamed;

        return null;
    }

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

        if (RenamedBuiltInAliases.TryGetValue(name.Trim(), out var renamed))
        {
            var renamedParameter = element.get_Parameter(renamed);
            if (renamedParameter is not null)
                return LiftValuelessToType(doc, element, renamedParameter);

            var renamedTypeParameter = FindOnType(doc, element, type => type.get_Parameter(renamed));
            if (renamedTypeParameter is not null)
                return renamedTypeParameter;
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
    /// view on instances (live cases 5/8 #468/#469); a string view may also be
    /// HasValue with an EMPTY string while the value lives on the type (live
    /// case 5, C5-05: Assembly Code on a wall). When the instance view carries
    /// no value and the SAME parameter on the element's type has one, return
    /// the type-side parameter: reads see the real value, writes land where
    /// the parameter actually lives. Genuinely empty both sides keep the
    /// instance view (honest holes stay holes). A parameter the element stores
    /// ITSELF (a two-sided built-in such as IFC GUID) never lifts: its blank
    /// is the instance's own value, not a window onto the type (#478 review).
    /// The storage check runs only when a lift would actually change the
    /// result, so blank-both reads pay nothing extra.
    /// </summary>
    internal static Parameter? LiftValuelessToType(Document doc, Element element, Parameter candidate)
    {
        if (!CarriesNoValue(candidate))
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

        if (onType is null || CarriesNoValue(onType))
            return candidate;

        // A parameter the element stores ITSELF (a two-sided built-in such as
        // IFC GUID) keeps its blank as its own value — the instance's state,
        // not a window onto the type-side value; lifting it would misdirect
        // writes and notExists onto the type. Only a view the element does not
        // own (the Assembly Code) may lift.
        if (IsStoredOnElement(element, candidate))
            return candidate;

        return onType;
    }

    /// <summary>
    /// Whether the element lists the parameter among its OWN parameters. A
    /// parameter handed to an instance while the value actually lives on its
    /// type is absent from element.Parameters (Autodesk forum, "Assembly Code
    /// instance/type parameter bug": the parameter is in exactly one of
    /// element.Parameters / type.Parameters). The write path relies on the
    /// same membership test (IsExposed) for its not_supported verdicts.
    /// </summary>
    private static bool IsStoredOnElement(Element element, Parameter candidate)
    {
        try
        {
            var id = candidate.Id;
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter.Id == id)
                    return true;
            }

            return false;
        }
        catch (Exception)
        {
            return true; // Cannot tell: keep the honest hole, never misdirect a write.
        }
    }

    /// <summary>
    /// True when the parameter has no usable value: no value at all, or a blank
    /// string. Such an instance view of a type-bound parameter must not shadow
    /// the real value stored on the type.
    /// </summary>
    private static bool CarriesNoValue(Parameter parameter)
    {
        if (!parameter.HasValue)
            return true;

        try
        {
            return parameter.StorageType == StorageType.String
                   && string.IsNullOrWhiteSpace(parameter.AsString());
        }
        catch (Exception)
        {
            return false;
        }
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