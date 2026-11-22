using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetElementDetailsTool : AgentReadToolBase
{
    private const int MaxElements = 10;
    private const int DefaultMaxParametersPerElement = 50;
    private const int HardMaxParametersPerElement = 200;

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
            "its type parameters. Parameters are classified as system, shared or family: by " +
            "default only system and shared are returned, and the number of omitted family-local " +
            "geometry parameters is reported in familyParametersOmitted; pass " +
            "parameterClasses=[\"family\"] to include them. instanceParametersTotal/Returned/" +
            "Truncated and parametersTotal/Returned/Truncated describe the caps (the cap applies " +
            "per scope). readOnlyReason is set for parameters the agent cannot write. Narrow the " +
            "payload with parameterNames, parameterNameContains, excludeDerived; onlyNonEmpty " +
            "defaults to true (empty values are hidden, pass false to list them).",
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
        var parameterNames = new HashSet<string>(
            AgentJson.GetStringArray(args, "parameterNames"),
            StringComparer.OrdinalIgnoreCase);

        var elements = new List<ElementDetails>();
        var types = new List<ElementTypeDetails>();
        var typesById = new Dictionary<long, ElementTypeDetails>();
        var missing = new List<long>();

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
            var filter = new AgentParameterFilter
            {
                Classes = classes,
                Names = parameterNames,
                NameContains = parameterNameContains,
                OnlyNonEmpty = onlyNonEmpty,
                ExcludeDerived = excludeDerived,
                Max = maxParameters,
                EssenceProfile = AgentElementEssence.ProfileFor(element.Category)
            };

            if (type is not null)
            {
                var typeId = type.Id.GetValue();
                if (!typesById.TryGetValue(typeId, out var typeDetails))
                {
                    typeDetails = AgentElementDetailsBuilder.BuildType(
                        doc,
                        type,
                        filter,
                        includeTypeParameters);
                    typesById[typeId] = typeDetails;
                    types.Add(typeDetails);
                }
            }

            elements.Add(AgentElementDetailsBuilder.BuildElement(doc, element, type, filter));
        }

        if (elements.Count == 0)
        {
            return ToolResult.Fail(
                "element_not_found",
                "None of the requested element ids exist in this document.");
        }

        var payload = new
        {
            total = elements.Count,
            missing,
            parameterClasses = classes.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            includeTypeParameters,
            types,
            elements
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Returned details for " + elements.Count + " element(s) and " + types.Count +
            " type(s).");
    }

    private static ElementType? ResolveType(Document doc, Element element)
    {
        var typeId = element.GetTypeId();
        if (typeId == ElementId.InvalidElementId)
            return null;

        return doc.GetElement(typeId) as ElementType;
    }
}