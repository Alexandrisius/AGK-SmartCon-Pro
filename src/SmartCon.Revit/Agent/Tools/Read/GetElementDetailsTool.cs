using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetElementDetailsTool : AgentReadToolBase
{
    private const int MaxElements = 20;
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
            "Returns full parameter details for up to 20 elements: name, category, family, type, " +
            "level, room and every parameter with its raw value, display value, storage type, " +
            "read-only/shared flags and scope (instance or type). Use it after query_elements " +
            "when you need parameter values or types before proposing changes.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""elementIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":20},""includeTypeParameters"":{""type"":""boolean"",""default"":true}},""required"":[""elementIds""],""additionalProperties"":false}"
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

        var includeTypeParameters = AgentJson.GetBool(args, "includeTypeParameters", true);
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

            details.Add(CreateDetails(doc, element, includeTypeParameters, ct));
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