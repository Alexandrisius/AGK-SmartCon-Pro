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
            "Counts elements matched by a QuerySpec and groups them by category, level, type, " +
            "family or a parameter value. Optionally sums a numeric parameter (sumParameter) per group. " +
            "Use it for questions like 'how many walls are on each level' or " +
            "'total door width per fire rating' or 'how many elements of each family'. " +
            "groupBy=type groups carry family, typeId and category next to key (the type name); " +
            "groupBy=family counts per family name. " +
            "Returns at most topN groups " +
            "(default 20, largest first); excludeInternal=true (default) drops the " +
            "'(no category)' bucket. Query rules are the same as in query_elements: a condition " +
            "parameter is a parameter name, a BuiltInParameter id or a structural field " +
            "(name, family, type, category, level — reserved words, string operators only), and " +
            "a parameter missing on every scanned element returns unknown_parameter instead of " +
            "an empty list. " +
            "includeZeroCount=true adds count:0 groups for every requested category " +
            "(proves 'the category is empty' in one call).",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""},""categories"":{""type"":""array"",""items"":{""type"":""string""}},""levelIds"":{""type"":""array"",""items"":{""type"":""integer""}},""typeIds"":{""type"":""array"",""items"":{""type"":""integer""}},""viewId"":{""type"":""integer""},""conditions"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""parameter"":{""type"":""string""},""op"":{""type"":""string"",""enum"":[""eq"",""neq"",""gt"",""gte"",""lt"",""lte"",""contains"",""startsWith"",""endsWith"",""in"",""notIn"",""exists"",""notExists""]},""value"":{},""unit"":{""type"":""string""}},""required"":[""parameter"",""op""],""additionalProperties"":false}},""logic"":{""type"":""string"",""enum"":[""and"",""or""],""default"":""and""},""includeTypeParameters"":{""type"":""boolean"",""default"":false},""groupBy"":{""type"":""string"",""enum"":[""category"",""level"",""type"",""family"",""parameter""],""default"":""category""},""parameter"":{""type"":""string""},""sumParameter"":{""type"":""string""},""topN"":{""type"":""integer"",""minimum"":1,""maximum"":200,""default"":20},""includeZeroCount"":{""type"":""boolean"",""default"":false},""excludeInternal"":{""type"":""boolean"",""default"":true}},""required"":[""groupBy""],""additionalProperties"":false}"
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
        if (groupBy is not ("category" or "level" or "type" or "family" or "parameter"))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "groupBy must be one of: category, level, type, family, parameter.");
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
                AgentCategoryResolver.SuggestSuffix(doc, result.UnknownCategories[0]) +
                ". Use list_categories to see available names.");
        }

        if (result.UnknownParameterFailure() is { } unknownParameter)
        {
            return unknownParameter;
        }

        var groups = new Dictionary<string, GroupAccumulator>(StringComparer.OrdinalIgnoreCase);
        var withIdentity = groupBy is "type" or "family";
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
            if (withIdentity)
            {
                accumulator.AddIdentity(doc, element, groupBy == "type");
            }

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
            var suggestions = AgentQueryFields.SuggestParameterNames(
                doc, result.Elements[0], groupParameter!);
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
        var zeroCountUnknown = new List<string>();
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
            zeroCountUnknown.AddRange(unknownForZero);
            foreach (var categoryId in requestedIds)
            {
                var category = Category.GetCategory(doc, categoryId);
                var name = category?.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    // Some obsolete built-in category ids have no Category object;
                    // without the fallback the promised count:0 row silently vanished.
                    var raw = categoryId.GetValue();
                    if (raw >= int.MinValue && raw <= int.MaxValue)
                        name = ((BuiltInCategory)(int)raw).ToString();
                }

                if (string.IsNullOrWhiteSpace(name) || groups.ContainsKey(name!))
                    continue;

                groups[name!] = new GroupAccumulator();
            }
        }

        var grouped = groups
            .Select(pair => pair.Value.ToGroup(pair.Key, sumUnit))
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
        var typeBound = AgentQueryTypeHint.Describe(doc, spec, result);
        var payload = new
        {
            total = result.Total,
            scanned = result.Elements.Count,
            truncated = result.Total > result.Elements.Count,
            groupBy,
            groupsTotal = grouped.Count,
            groupsSkippedInternal = skippedInternal,
            zeroCountIncluded = includeZeroCount,
            zeroCountUnknown = zeroCountUnknown.Count > 0 ? zeroCountUnknown : null,
            groupsTruncated = grouped.Count > topN,
            typeBoundParameter = typeBound is null
                ? null
                : new { parameter = typeBound.Parameter, types = typeBound.TypeCount },
            groups = grouped.Take(topN).ToList()
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Summarized " + result.Total + " element(s) into " + grouped.Count + " group(s)." +
            (grouped.Count > topN
                ? " Showing top " + topN + " of " + grouped.Count + " groups."
                : string.Empty) +
            (typeBound is null ? string.Empty : " " + typeBound.Message));
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

            case "family":
                return AgentQueryFields.ReadFamily(doc, element) ?? "(no family)";

            case "parameter":
                if (QuerySpec.TryGetStructuralField(groupParameter!, out var field))
                {
                    var texts = AgentQueryFields.Read(doc, element, field);
                    parameterFound = texts.Count > 0;
                    return texts.Count == 0 ? "(no " + field + ")" : texts[0];
                }

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

    private sealed class GroupAccumulator
    {
        private bool _typeMixed;

        private bool _familyMixed;

        private bool _categoryMixed;

        public int Count { get; set; }

        public double? Sum { get; set; }

        public long? TypeId { get; private set; }

        public string? Family { get; private set; }

        public string? Category { get; private set; }

        /// <summary>
        /// Family, type and category of the group's elements, for groupBy=type
        /// and groupBy=family. The key is a name: the same type or family name
        /// in two places lands in one group, and one identity would then be
        /// wrong for part of the count — such a field is reported as unknown.
        /// </summary>
        public void AddIdentity(Document doc, Element element, bool withType)
        {
            var family = AgentQueryFields.ReadFamily(doc, element);
            var typeId = element.GetTypeId();
            var typeIdValue = typeId == ElementId.InvalidElementId ? (long?)null : typeId.GetValue();
            var category = element.Category?.Name;

            if (Count == 1)
            {
                Family = family;
                TypeId = withType ? typeIdValue : null;
                Category = category;
                return;
            }

            _familyMixed |= !string.Equals(Family, family, StringComparison.OrdinalIgnoreCase);
            _categoryMixed |= !string.Equals(Category, category, StringComparison.OrdinalIgnoreCase);
            if (withType)
                _typeMixed |= TypeId != typeIdValue;
        }

        public ElementSummaryGroup ToGroup(string key, string? sumUnit) => new()
        {
            Key = key,
            Count = Count,
            Sum = Sum,
            SumUnit = Sum.HasValue ? sumUnit : null,
            Family = _familyMixed ? null : Family,
            TypeId = _typeMixed ? null : TypeId,
            Category = _categoryMixed ? null : Category
        };
    }
}
