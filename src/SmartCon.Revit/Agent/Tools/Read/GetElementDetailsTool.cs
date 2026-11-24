using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetElementDetailsTool : AgentReadToolBase
{
    private const int MaxElements = 10;
    private const int DefaultMaxParametersPerElement = 50;
    private const int HardMaxParametersPerElement = 200;
    private const int MinCompactedParameters = 4;

    /// <summary>
    /// Payload size the tool compacts to: the orchestrator budget minus room for
    /// the envelope (success/message/affectedCount) and the compaction note.
    /// </summary>
    private const int PayloadTargetCharacters =
        ToolResultBudget.DefaultMaxCharacters - 800;

    public GetElementDetailsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_element_details",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Reads up to 10 elements with their definitions. Each element returns identity " +
            "(category, family, type, typeId, familyId, familyKind, level, room), a compact " +
            "essence of key built-in values, and its instance parameters. Each distinct type is " +
            "returned once in types[] with its own essence and, when includeTypeParameters=true, " +
            "its type parameters - except an element type asked for by its own typeId, whose " +
            "parameters are always returned. Parameters are classified as system, shared or family: by " +
            "default only system and shared are returned, and the number of omitted family-local " +
            "geometry parameters is reported in familyParametersOmitted; pass " +
            "parameterClasses=[\"family\"] to include them. instanceParametersTotal/Returned/" +
            "Truncated and parametersTotal/Returned/Truncated describe the caps (the cap applies " +
            "per scope). readOnlyReason is set for parameters the agent cannot write. Narrow the " +
            "payload with parameterNames, parameterNameContains, excludeDerived; onlyNonEmpty " +
            "defaults to true (empty values are hidden, pass false to list them - an empty " +
            "parameter then carries value:\"\" and hasValue:false, so empty is distinct from " +
            "not returned). When the batch exceeds the context budget the per-scope parameter " +
            "cap is compacted (parametersCompacted) so every requested element stays in the " +
            "answer. A name of " +
            "parameterNames that the element itself does not carry is looked up on its type " +
            "(the type parameters join the answer for that element); names found nowhere are " +
            "listed in parameterNamesNotFound.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""},""elementIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":10},""parameterClasses"":{""type"":""array"",""items"":{""type"":""string"",""enum"":[""system"",""shared"",""family""]}},""includeTypeParameters"":{""type"":""boolean"",""default"":false},""parameterNames"":{""type"":""array"",""items"":{""type"":""string""}},""parameterNameContains"":{""type"":""string""},""onlyNonEmpty"":{""type"":""boolean"",""default"":true},""excludeDerived"":{""type"":""boolean"",""default"":false},""maxParametersPerElement"":{""type"":""integer"",""minimum"":1,""maximum"":200,""default"":50}},""required"":[""elementIds""],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var elementIds = AgentJson.GetLongArray(args, "elementIds");
        if (elementIds.Count == 0)
            return ToolResult.Fail("invalid_arguments", "elementIds must contain at least one id.");

        if (elementIds.Count > MaxElements)
            return ToolResult.Fail(
                "too_many_elements",
                "At most " + MaxElements + " element ids are allowed per call.");

        var classes = new HashSet<string>(StringComparer.Ordinal);
        var requestedClasses = AgentJson.GetStringArray(args, "parameterClasses");
        if (requestedClasses.Count == 0)
        {
            classes.Add(AgentParameterClassifier.System);
            classes.Add(AgentParameterClassifier.Shared);
        }
        else
        {
            foreach (var raw in requestedClasses)
            {
                var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
                if (value != AgentParameterClassifier.System
                    && value != AgentParameterClassifier.Shared
                    && value != AgentParameterClassifier.Family)
                {
                    return ToolResult.Fail(
                        "invalid_arguments",
                        "parameterClasses supports only \"system\", \"shared\" and \"family\".");
                }

                classes.Add(value);
            }
        }

        var includeTypeParameters = AgentJson.GetBool(args, "includeTypeParameters", false);
        var onlyNonEmpty = AgentJson.GetBool(args, "onlyNonEmpty", true);
        var excludeDerived = AgentJson.GetBool(args, "excludeDerived", false);
        var parameterNameContains = AgentJson.GetString(args, "parameterNameContains");
        var maxParameters = Math.Max(
            1,
            Math.Min(
                AgentJson.GetInt(args, "maxParametersPerElement", DefaultMaxParametersPerElement),
                HardMaxParametersPerElement));
        var requestedNames = AgentJson.GetStringArray(args, "parameterNames");
        var parameterNames = new HashSet<string>(requestedNames, StringComparer.OrdinalIgnoreCase);
        var nameBuiltIns = parameterNames.Count > 0
            ? AgentParameterResolver.BuiltInsOf(parameterNames.Where(name => !string.IsNullOrEmpty(name))!)
            : new HashSet<BuiltInParameter>();

        // Pass 1 resolves the ids and the per-request flags once - none of that
        // depends on the parameter cap compacted below.
        var missing = new List<long>();
        // The requested names that matched no parameter of any element or its
        // type (reported at the end): a name that lives only on the type pulls
        // the type parameters in for that element instead of disappearing.
        var namesFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requests = new List<ElementRequest>();
        foreach (var id in elementIds)
        {
            ct.ThrowIfCancellationRequested();

            var element = doc.GetElement(ElementIdCompat.Create(id));
            if (element is null)
            {
                missing.Add(id);
                continue;
            }

            var type = element as ElementType ?? ResolveType(doc, element);

            // Every element is checked on its own: a name found on the type of
            // the first element says nothing about the next one, which may have
            // another type (namesFound only decides the not-found list).
            var needTypeParameters = false;
            if (parameterNames.Count > 0 && type is not null)
            {
                var ownParameters = element is ElementType ? type.Parameters : element.Parameters;
                foreach (var name in requestedNames)
                {
                    if (HasParameterNamed(ownParameters, name, nameBuiltIns))
                    {
                        namesFound.Add(name);
                        continue;
                    }

                    // A name the element itself does not carry may live on its
                    // type: the type parameters join the answer for this element.
                    if (element is not ElementType
                        && HasParameterNamed(type.Parameters, name, nameBuiltIns))
                    {
                        namesFound.Add(name);
                        needTypeParameters = true;
                    }
                }
            }

            requests.Add(new ElementRequest(
                element, type, element is ElementType, needTypeParameters));
        }

        if (requests.Count == 0)
        {
            return ToolResult.Fail(
                "element_not_found",
                "None of the requested element ids exist in this document.");
        }

        var parameterNamesNotFound = requestedNames.Count == 0
            ? null
            : requestedNames.Where(name => !namesFound.Contains(name)).ToList();
        if (parameterNamesNotFound is { Count: 0 })
            parameterNamesNotFound = null;

        // Pass 2 builds the payload. Live case 4 round 3: full type dumps pushed
        // the result past the context budget, and the generic trimmer then threw
        // away REQUESTED ELEMENTS - the model had to repeat the call. Instead the
        // tool itself compacts the parameter rows (fewer per scope) until the
        // whole batch fits; every requested element stays in the answer.
        var filterTemplate = new AgentParameterFilter
        {
            Classes = classes,
            Names = parameterNames,
            NameContains = parameterNameContains,
            OnlyNonEmpty = onlyNonEmpty,
            ExcludeDerived = excludeDerived,
            Max = maxParameters
        };
        var cap = maxParameters;
        List<ElementDetails> elements;
        List<ElementTypeDetails> types;
        Dictionary<string, object?> payload;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            (elements, types) = BuildDetails(
                doc, requests, filterTemplate, cap, includeTypeParameters, ct);
            payload = new Dictionary<string, object?>
            {
                ["total"] = elements.Count,
                ["missing"] = missing,
                ["parameterClasses"] = classes.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                ["includeTypeParameters"] = includeTypeParameters,
                ["types"] = types,
                ["elements"] = elements
            };
            // A null dictionary value would serialize as an explicit null key
            // (unlike the null property of the old anonymous payload): only the
            // real list belongs in the answer.
            if (parameterNamesNotFound is not null)
                payload["parameterNamesNotFound"] = parameterNamesNotFound;
            if (AgentJson.Serialize(payload).GetRawText().Length <= PayloadTargetCharacters
                || cap <= MinCompactedParameters)
            {
                break;
            }

            cap = Math.Max(MinCompactedParameters, cap / 2);
        }

        var messageText = "Returned details for " + elements.Count + " element(s) and " +
                          types.Count + " type(s).";
        if (cap < maxParameters)
        {
            payload["parametersCompacted"] = new { requested = maxParameters, compactedTo = cap };
            messageText += " All " + elements.Count + " requested element(s) are kept; " +
                           "parameters per scope were compacted to " + cap +
                           " to fit the context budget - pass parameterNames " +
                           "(or parameterNameContains) to read specific values.";
        }

        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
    }

    private (List<ElementDetails> Elements, List<ElementTypeDetails> Types) BuildDetails(
        Document doc,
        List<ElementRequest> requests,
        AgentParameterFilter template,
        int maxParameters,
        bool includeTypeParameters,
        CancellationToken ct)
    {
        var elements = new List<ElementDetails>(requests.Count);
        var types = new List<ElementTypeDetails>();
        var typesById = new Dictionary<long, ElementTypeDetails>();
        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();
            var filter = template.Clone(maxParameters, request.Element.Category);
            if (request.Type is not null)
            {
                var typeId = request.Type.Id.GetValue();
                if (!typesById.TryGetValue(typeId, out var typeDetails))
                {
                    typeDetails = AgentElementDetailsBuilder.BuildType(
                        doc,
                        request.Type,
                        filter,
                        // A type asked for by its own id is the element: its
                        // parameters are what the call is about, flag or not.
                        includeTypeParameters || request.TypeIsTheElement || request.NeedTypeParameters);
                    typesById[typeId] = typeDetails;
                    types.Add(typeDetails);
                }
                else if ((request.TypeIsTheElement || request.NeedTypeParameters)
                         && typeDetails.Parameters is null)
                {
                    // The type was listed through an earlier element without its
                    // parameters; now they are asked for - by the type's own id
                    // or by a name that lives on it - so it is rebuilt with them.
                    var rebuilt = AgentElementDetailsBuilder.BuildType(
                        doc, request.Type, filter, includeTypeParameters: true);
                    types[types.IndexOf(typeDetails)] = rebuilt;
                    typesById[typeId] = rebuilt;
                }
            }

            elements.Add(
                AgentElementDetailsBuilder.BuildElement(doc, request.Element, request.Type, filter));
        }

        return (elements, types);
    }

    private sealed record ElementRequest(
        Element Element,
        ElementType? Type,
        bool TypeIsTheElement,
        bool NeedTypeParameters);

    /// <summary>
    /// Whether the parameter set carries the requested name: by display name or
    /// by the built-in id the name stands for (an enum id or an alias), the same
    /// two ways the write tools resolve names.
    /// </summary>
    private static bool HasParameterNamed(
        ParameterSet parameters,
        string name,
        HashSet<BuiltInParameter> nameBuiltIns)
    {
        foreach (Parameter parameter in parameters)
        {
            var displayName = parameter.Definition?.Name;
            if (!string.IsNullOrEmpty(displayName)
                && string.Equals(displayName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (AgentParameterClassifier.IsBuiltIn(parameter, out var builtIn)
                && nameBuiltIns.Contains(builtIn))
            {
                return true;
            }
        }

        return false;
    }

    private static ElementType? ResolveType(Document doc, Element element)
    {
        var typeId = element.GetTypeId();
        if (typeId == ElementId.InvalidElementId)
            return null;

        return doc.GetElement(typeId) as ElementType;
    }
}