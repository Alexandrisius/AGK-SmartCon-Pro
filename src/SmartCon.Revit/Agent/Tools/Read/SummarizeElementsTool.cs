using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class SummarizeElementsTool : AgentReadToolBase
{
    private const int MaxElementsForSummary = 20000;
    private const int MaxGroups = 200;

    public SummarizeElementsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "summarize_elements",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Counts elements matched by a QuerySpec and groups them by category, level, type or " +
            "a parameter value. Optionally sums a numeric parameter (sumParameter) per group. " +
            "Use it for questions like 'how many walls are on each level' or " +
            "'total door width per fire rating'. Query rules are the same as in query_elements.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""categories"":{""type"":""array"",""items"":{""type"":""string""}},""levelIds"":{""type"":""array"",""items"":{""type"":""integer""}},""typeIds"":{""type"":""array"",""items"":{""type"":""integer""}},""viewId"":{""type"":""integer""},""conditions"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""parameter"":{""type"":""string""},""op"":{""type"":""string"",""enum"":[""eq"",""neq"",""gt"",""gte"",""lt"",""lte"",""contains"",""startsWith"",""endsWith"",""in"",""notIn"",""exists"",""notExists""]},""value"":{},""unit"":{""type"":""string""}},""required"":[""parameter"",""op""],""additionalProperties"":false}},""logic"":{""type"":""string"",""enum"":[""and"",""or""],""default"":""and""},""includeTypeParameters"":{""type"":""boolean"",""default"":false},""groupBy"":{""type"":""string"",""enum"":[""category"",""level"",""type"",""parameter""],""default"":""category""},""parameter"":{""type"":""string""},""sumParameter"":{""type"":""string""}},""required"":[""groupBy""],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        QuerySpec? spec;
        try
        {
            spec = AgentJson.Deserialize<QuerySpec>(args);
        }
        catch (JsonException ex)
        {
            return ToolResult.Fail(
                "invalid_query",
                "The query is not valid JSON for QuerySpec: " + ex.Message);
        }

        if (spec is null)
            return ToolResult.Fail("invalid_query", "The query is empty.");

        var errors = spec.Validate();
        if (errors.Count > 0)
            return ToolResult.Fail("invalid_query", string.Join("; ", errors));

        var groupBy = (AgentJson.GetString(args, "groupBy") ?? "category").ToLowerInvariant();
        if (groupBy is not ("category" or "level" or "type" or "parameter"))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "groupBy must be one of: category, level, type, parameter.");
        }

        var groupParameter = AgentJson.GetString(args, "parameter");
        if (groupBy == "parameter" && string.IsNullOrWhiteSpace(groupParameter))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "groupBy=parameter requires the 'parameter' argument.");
        }

        var sumParameter = AgentJson.GetString(args, "sumParameter");

        var query = new AgentElementQuery(doc);
        var result = query.Execute(spec, MaxElementsForSummary, ct);
        if (result.UnknownCategories.Count > 0)
        {
            return ToolResult.Fail(
                "unknown_category",
                "Unknown categories: " + string.Join(", ", result.UnknownCategories) +
                ". Use list_categories to see available names.");
        }

        var groups = new Dictionary<string, GroupAccumulator>(StringComparer.OrdinalIgnoreCase);
        string? sumUnit = null;

        foreach (var element in result.Elements)
        {
            ct.ThrowIfCancellationRequested();

            var key = ReadGroupKey(doc, element, groupBy, groupParameter, spec.IncludeTypeParameters);
            if (!groups.TryGetValue(key, out var accumulator))
            {
                accumulator = new GroupAccumulator();
                groups[key] = accumulator;
            }

            accumulator.Count++;

            if (!string.IsNullOrWhiteSpace(sumParameter))
            {
                var parameter = AgentParameterResolver.Find(
                    doc,
                    element,
                    sumParameter!,
                    spec.IncludeTypeParameters);

                var measured = AgentParameterValue.ReadMeasured(doc, parameter);
                if (measured is { } value)
                {
                    accumulator.Sum = (accumulator.Sum ?? 0) + value.Value;
                    sumUnit ??= value.Unit;
                }
            }
        }

        var grouped = groups
            .Select(pair => new ElementSummaryGroup
            {
                Key = pair.Key,
                Count = pair.Value.Count,
                Sum = pair.Value.Sum,
                SumUnit = pair.Value.Sum.HasValue ? sumUnit : null
            })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var payload = new
        {
            total = result.Total,
            scanned = result.Elements.Count,
            truncated = result.Total > result.Elements.Count,
            groupBy,
            groupsTruncated = grouped.Count > MaxGroups,
            groups = grouped.Take(MaxGroups).ToList()
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Summarized " + result.Total + " element(s) into " + grouped.Count + " group(s).");
    }

    private static string ReadGroupKey(
        Document doc,
        Element element,
        string groupBy,
        string? groupParameter,
        bool includeTypeParameters)
    {
        switch (groupBy)
        {
            case "level":
                var levelId = element.LevelId;
                if (levelId != ElementId.InvalidElementId
                    && doc.GetElement(levelId) is Level level)
                {
                    return level.Name;
                }

                return "(no level)";

            case "type":
                var typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId
                    && doc.GetElement(typeId) is Element type)
                {
                    return type.Name;
                }

                return "(no type)";

            case "parameter":
                var parameter = AgentParameterResolver.Find(
                    doc,
                    element,
                    groupParameter!,
                    includeTypeParameters);

                if (parameter is null || !parameter.HasValue)
                    return "(empty)";

                var value = parameter.StorageType == StorageType.String
                    ? parameter.AsString()
                    : parameter.AsValueString();

                return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;

            default:
                return AgentElementCardFactory.GetCategoryName(element.Category)
                       ?? element.Category?.Name
                       ?? "(no category)";
        }
    }

    private sealed class GroupAccumulator
    {
        public int Count { get; set; }

        public double? Sum { get; set; }
    }
}