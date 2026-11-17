using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetElementDetailsTool : AgentReadToolBase
{
    private const int MaxElements = 10;
    private const int MaxParametersPerElement = 200;

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
            "Returns parameter details for up to 10 elements: name, category, family, type, " +
            "level, room and parameters with their raw value, display value, storage type, " +
            "read-only/shared flags and scope (instance or type). Type parameters are included " +
            "only when includeTypeParameters=true. Narrow the payload with parameterNames " +
            "(exact names) and onlyNonEmpty=true. Use it after query_elements before proposing changes.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":10},""includeTypeParameters"":{""type"":""boolean"",""default"":false},""parameterNames"":{""type"":""array"",""items"":{""type"":""string""}},""onlyNonEmpty"":{""type"":""boolean"",""default"":false}},""required"":[""elementIds""],""additionalProperties"":false}"
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

        var includeTypeParameters = AgentJson.GetBool(args, "includeTypeParameters", false);
        var onlyNonEmpty = AgentJson.GetBool(args, "onlyNonEmpty", false);
        var parameterNames = new HashSet<string>(
            AgentJson.GetStringArray(args, "parameterNames"),
            StringComparer.OrdinalIgnoreCase);
        var details = new List<ElementDetails>();
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

            details.Add(CreateDetails(
                doc,
                element,
                includeTypeParameters,
                onlyNonEmpty,
                parameterNames,
                ct));
        }

        if (details.Count == 0)
        {
            return ToolResult.Fail(
                "element_not_found",
                "None of the requested element ids exist in this document.");
        }

        var payload = new
        {
            total = details.Count,
            missing,
            elements = details
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Returned details for " + details.Count + " element(s).");
    }

    private static ElementDetails CreateDetails(
        Document doc,
        Element element,
        bool includeTypeParameters,
        bool onlyNonEmpty,
        HashSet<string> parameterNames,
        CancellationToken ct)
    {
        var card = AgentElementCardFactory.Create(doc, element);
        var parameters = new List<ParameterValueInfo>();
        var truncated = false;

        foreach (Parameter parameter in element.Parameters)
        {
            if (ct.IsCancellationRequested)
                ct.ThrowIfCancellationRequested();

            if (parameters.Count >= MaxParametersPerElement)
            {
                truncated = true;
                break;
            }

            if (ShouldInclude(parameter, onlyNonEmpty, parameterNames))
                parameters.Add(CreateParameterInfo(parameter, "instance"));
        }

        if (includeTypeParameters && !truncated)
        {
            var typeId = element.GetTypeId();
            if (typeId != ElementId.InvalidElementId
                && doc.GetElement(typeId) is Element type)
            {
                foreach (Parameter parameter in type.Parameters)
                {
                    ct.ThrowIfCancellationRequested();
                    if (parameters.Count >= MaxParametersPerElement)
                    {
                        truncated = true;
                        break;
                    }

                    if (ShouldInclude(parameter, onlyNonEmpty, parameterNames))
                        parameters.Add(CreateParameterInfo(parameter, "type"));
                }
            }
        }

        return new ElementDetails
        {
            UniqueId = element.UniqueId,
            ElementId = element.Id.GetValue(),
            Category = card.Category,
            Family = card.Family,
            Type = card.Type,
            Name = card.Name,
            LevelName = card.LevelName,
            Room = card.Room,
            ParametersTruncated = truncated,
            Parameters = parameters
        };
    }

    private static bool ShouldInclude(
        Parameter parameter,
        bool onlyNonEmpty,
        HashSet<string> parameterNames)
    {
        var name = parameter.Definition?.Name;
        if (parameterNames.Count > 0
            && (string.IsNullOrEmpty(name) || !parameterNames.Contains(name!)))
        {
            return false;
        }

        if (!onlyNonEmpty)
            return true;

        try
        {
            if (!parameter.HasValue)
                return false;

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

    private static ParameterValueInfo CreateParameterInfo(Parameter parameter, string scope)
    {
        var isShared = false;
        string? guid = null;
        try
        {
            isShared = parameter.IsShared;
            if (isShared)
            {
                var value = parameter.GUID;
                if (value != Guid.Empty)
                    guid = value.ToString("D");
            }
        }
        catch (Exception)
        {
        }

        return new ParameterValueInfo
        {
            Name = parameter.Definition?.Name ?? string.Empty,
            Value = TryReadRawValue(parameter),
            DisplayValue = TryReadDisplayValue(parameter),
            StorageType = parameter.StorageType.ToString(),
            IsReadOnly = parameter.IsReadOnly,
            IsShared = isShared,
            ParameterGuid = guid,
            Scope = scope
        };
    }

    private static string? TryReadRawValue(Parameter parameter)
    {
        if (!parameter.HasValue)
            return null;

        try
        {
            return parameter.StorageType switch
            {
                StorageType.String => parameter.AsString(),
                StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
                StorageType.Double => parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture),
                StorageType.ElementId => parameter.AsElementId().GetValue()
                    .ToString(CultureInfo.InvariantCulture),
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadDisplayValue(Parameter parameter)
    {
        if (!parameter.HasValue)
            return null;

        try
        {
            var value = parameter.AsValueString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}