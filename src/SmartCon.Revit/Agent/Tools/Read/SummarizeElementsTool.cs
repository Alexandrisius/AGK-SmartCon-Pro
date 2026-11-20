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
    private const int DefaultTopGroups = 20;
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
            "'total door width per fire rating'. Returns at most topN groups " +
            "(default 20, largest first); excludeInternal=true (default) drops the " +
            "'(no category)' bucket. Query rules are the same as in query_elements. " +
            "includeZeroCount=true adds count:0 groups for every requested category " +
            "(proves 'the category is empty' in one call).",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""categories"":{""type"":""array"",""items"":{""type"":""string""}},""levelIds"":{""type"":""array"",""items"":{""type"":""integer""}},""typeIds"":{""type"":""array"",""items"":{""type"":""integer""}},""viewId"":{""type"":""integer""},""conditions"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""parameter"":{""type"":""string""},""op"":{""type"":""string"",""enum"":[""eq"",""neq"",""gt"",""gte"",""lt"",""lte"",""contains"",""startsWith"",""endsWith"",""in"",""notIn"",""exists"",""notExists""]},""value"":{},""unit"":{""type"":""string""}},""required"":[""parameter"",""op""],""additionalProperties"":false}},""logic"":{""type"":""string"",""enum"":[""and"",""or""],""default"":""and""},""includeTypeParameters"":{""type"":""boolean"",""default"":false},""groupBy"":{""type"":""string"",""enum"":[""category"",""level"",""type"",""parameter""],""default"":""category""},""parameter"":{""type"":""string""},""sumParameter"":{""type"":""string""},""topN"":{""type"":""integer"",""minimum"":1,""maximum"":200,""default"":20},""includeZeroCount"":{""type"":""boolean"",""default"":false},""excludeInternal"":{""type"":""boolean"",""default"":true}},""required"":[""groupBy""],""additionalProperties"":false}"
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
        AgentQueryResult result;
        try
        {
            result = query.Execute(spec, MaxElementsForSummary, ct);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail("invalid_unit", ex.Message);
        }
        if (result.UnknownCategories.Count > 0)
        {
            return ToolResult.Fail(
                "unknown_category",
                "Unknown categories: " + string.Join(", ", result.UnknownCategories) +
                ". Use list_categories to see available names.");
        }

        var groups = new Dictionary<string, GroupAccumulator>(StringComparer.OrdinalIgnoreCase);
        string? sumUnit = null;
        var groupParameterResolved = false;

        foreach (var element in result.Elements)
        {
            ct.ThrowIfCancellationRequested();

            var key = ReadGroupKey(
                doc,
                element,
                groupBy,
                groupParameter,
                spec.IncludeTypeParameters,
                out var parameterFound);
            groupParameterResolved |= parameterFound;
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

        if (groupBy == "parameter" && !groupParameterResolved && result.Elements.Count > 0)
        {
            // The parameter may exist on the element TYPE while the caller asked for
            // instance-only scope. Saying "not found" there is a lie and costs a retry
            // loop (external run, A1).
            var foundOnType = !spec.IncludeTypeParameters
                              && AgentParameterResolver.Find(
                                  doc, result.Elements[0], groupParameter!, true) is not null;
            var suggestions = ParameterNameSuggestions(doc, result.Elements[0], groupParameter!);
            var message = foundOnType
                ? "Parameter '" + groupParameter + "' exists on the element type, but " +
                  "includeTypeParameters=false. Retry with includeTypeParameters=true."
                : "Parameter '" + groupParameter + "' was not found on the scanned " +
                  result.Elements.Count + " element(s)." +
                  (suggestions.Count > 0
                      ? " Did you mean " + string.Join(
                          " or ", suggestions.Select(name => "'" + name + "'")) + "?"
                      : string.Empty) +
                  " Use get_element_details(parameterNameContains=...) to find the exact name.";
            var data = JsonSerializer.SerializeToElement(new
            {
                unknownParameter = groupParameter,
                foundOnTypes = foundOnType,
                retryWith = foundOnType
                    ? new { includeTypeParameters = true }
                    : null,
                suggestions,
                scanned = result.Elements.Count
            });
            return ToolResult.Fail(
                foundOnType ? "parameter_on_type" : "unknown_parameter", message, data: data);
        }

        var includeZeroCount = AgentJson.GetBool(args, "includeZeroCount", false);
        if (includeZeroCount
            && groupBy == "category"
            && spec.Categories.Count > 0)
        {
            // "There are no elements of this category" is a valid and frequent answer
            // (empty schedules): return explicit zero rows for the requested categories
            // instead of forcing one call per category (external benchmark, task 4).
            var unknownForZero = new List<string>();
            var requestedIds = AgentCategoryResolver.Resolve(
                doc, spec.Categories, unknownForZero);
            foreach (var categoryId in requestedIds)
            {
                var category = Category.GetCategory(doc, categoryId);
                var name = category?.Name;
                if (string.IsNullOrWhiteSpace(name) || groups.ContainsKey(name!))
                    continue;

                groups[name!] = new GroupAccumulator();
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

        var excludeInternal = AgentJson.GetBool(args, "excludeInternal", true);
        var skippedInternal = 0;
        if (excludeInternal)
        {
            var before = grouped.Count;
            grouped = grouped.Where(group => !IsInternalGroupKey(group.Key)).ToList();
            skippedInternal = before - grouped.Count;
        }

        var topN = Math.Max(1, Math.Min(AgentJson.GetInt(args, "topN", DefaultTopGroups), MaxGroups));
        var payload = new
        {
            total = result.Total,
            scanned = result.Elements.Count,
            truncated = result.Total > result.Elements.Count,
            groupBy,
            groupsTotal = grouped.Count,
            groupsSkippedInternal = skippedInternal,
            zeroCountIncluded = includeZeroCount,
            groupsTruncated = grouped.Count > topN,
            groups = grouped.Take(topN).ToList()
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Summarized " + result.Total + " element(s) into " + grouped.Count + " group(s)." +
            (grouped.Count > topN
                ? " Showing top " + topN + " of " + grouped.Count + " groups."
                : string.Empty));
    }

    private static string ReadGroupKey(
        Document doc,
        Element element,
        string groupBy,
        string? groupParameter,
        bool includeTypeParameters,
        out bool parameterFound)
    {
        parameterFound = false;
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

                // Three different facts, three different labels: a missing
                // parameter is a data/name problem, HasValue=false is a real
                // "no value", and a blank string is an empty value. Collapsing
                // them into "(empty)" made the model re-run the query.
                if (parameter is null)
                    return "(no parameter)";

                parameterFound = true;
                if (!parameter.HasValue)
                    return "(no value)";

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

    private static bool IsInternalGroupKey(string key) =>
        string.IsNullOrWhiteSpace(key)
        || string.Equals(key, "(no category)", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Closest parameter names on the element (instance and type) for an unknown
    /// <c>groupBy=parameter</c> name, so a typo does not look like empty data.
    /// </summary>
    private static List<string> ParameterNameSuggestions(
        Document doc,
        Element element,
        string requested)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(element);
        var typeId = element.GetTypeId();
        if (typeId != ElementId.InvalidElementId && doc.GetElement(typeId) is Element type)
            Collect(type);

        var limit = Math.Max(2, (requested.Length + 1) / 2);
        return names
            // Never suggest the string the caller already passed: "Did you mean
            // 'Классификация систем'?" for the same request is noise (A1).
            .Where(name => !string.Equals(name, requested, StringComparison.OrdinalIgnoreCase))
            .Select(name => (
                Name: name,
                Distance: Distance(requested.ToLowerInvariant(), name.ToLowerInvariant())))
            .Where(pair => pair.Distance <= limit)
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Name, StringComparer.Ordinal)
            .Take(3)
            .Select(pair => pair.Name)
            .ToList();

        void Collect(Element source)
        {
            foreach (Parameter parameter in source.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name!);
            }
        }
    }

    private static int Distance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
            previous[index] = index;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[right.Length];
    }

    private sealed class GroupAccumulator
    {
        public int Count { get; set; }

        public double? Sum { get; set; }
    }
}