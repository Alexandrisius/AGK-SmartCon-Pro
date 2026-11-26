using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// The elements behind a schedule's rows. Revit has no "row → element" API, but a
/// filtered collector over the schedule view returns the elements the schedule
/// displays with its filters and phase already applied (a grouped schedule stays
/// instance-based; every instance row carries its typeId). Hidden columns do not
/// matter, unlike get_schedule_data (cell text only).
/// </summary>
public sealed class GetScheduleElementsTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;
    private const int MaxCollected = 50000;

    public GetScheduleElementsTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_schedule_elements",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Returns the element ids behind one schedule (scheduleId or exact scheduleName): " +
            "the instances the schedule displays, with typeId, family, type name and category. " +
            "This is the id bridge the other tools need: get_schedule_data " +
            "only returns cell text (hidden columns have no cells), get_schedule_definition " +
            "only describes the definition. The ids are collected from the schedule view, so " +
            "the schedule's filters and phase apply; hidden columns are included. A grouped " +
            "(non-itemized) schedule still yields its instances, each with typeId. Use " +
            "elementIds/typeId with set_parameters, query_elements or get_element_details. " +
            "byType=true returns one compact entry per type (typeId, names, category, " +
            "instanceCount) instead of the rows - the shape a type-scope write needs. " +
            "Paged with limit/offset (default 200, max 1000); total is the full count.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson +
                          @"""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""includeTypes"":{""type"":""boolean"",""default"":true,""description"":""Keep type elements if the schedule lists any (rare: most schedules list instances; instance rows always carry typeId/typeName). false keeps instance rows only.""},""byType"":{""type"":""boolean"",""default"":false,""description"":""Collapse the rows into one entry per element type: types[{typeId,typeName,familyName,category,instanceCount}], no paging needed""},""limit"":{""type"":""integer"",""default"":200,""maximum"":1000},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var scheduleId = AgentJson.GetLong(args, "scheduleId", 0);
        var scheduleName = AgentJson.GetString(args, "scheduleName");
        if (scheduleId == 0 && string.IsNullOrWhiteSpace(scheduleName))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "Pass scheduleId or scheduleName of the schedule to read (see list_schedules); " +
                "an empty call cannot know which schedule is meant.");
        }

        var schedule = AgentScheduleReader.FindSchedule(doc, scheduleId, scheduleName);
        if (schedule is null)
        {
            return ToolResult.Fail(
                "schedule_not_found",
                "Pass scheduleId or exact scheduleName of an existing schedule " +
                "(see list_schedules).");
        }

        var includeTypes = AgentJson.GetBool(args, "includeTypes", true);
        var byType = AgentJson.GetBool(args, "byType", false);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);

        List<ScheduleRow> rows;
        try
        {
            rows = CollectRows(doc, schedule, includeTypes, ct);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                "schedule_read_failed",
                "Revit could not collect the schedule elements: " + ex.GetType().Name + ": " +
                ex.Message);
        }

        var total = rows.Count;

        // byType collapses the rows into one entry per type: "53 typeId→category
        // pairs" instead of 437 row payloads, no paging, no follow-up summarize
        // call (S.47, #532).
        if (byType)
        {
            var types = rows
                .Where(row => row.TypeId.HasValue)
                .GroupBy(row => row.TypeId!.Value)
                .Select(group => new
                {
                    typeId = group.Key,
                    typeName = group.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.TypeName))
                        ?.TypeName,
                    familyName = group.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.FamilyName))
                        ?.FamilyName,
                    category = group.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.Category))
                        ?.Category,
                    instanceCount = group.Count()
                })
                .OrderByDescending(entry => entry.instanceCount)
                .ThenBy(entry => entry.typeId)
                .ToList();
            var withoutType = rows.Count(row => !row.TypeId.HasValue);
            return ToolResult.Ok(
                AgentJson.Serialize(new Dictionary<string, object?>
                {
                    ["scheduleId"] = schedule.Id.GetValue(),
                    ["scheduleName"] = schedule.Name,
                    ["total"] = total,
                    ["typesTotal"] = types.Count,
                    ["types"] = types,
                    ["rowsWithoutTypeId"] = withoutType > 0 ? withoutType : null,
                    ["note"] = "byType=true: one entry per element type of the schedule, " +
                               "instanceCount is how many of its elements the schedule lists. " +
                               "Use the typeIds with set_parameters(scope:type), " +
                               "summarize_elements or query_elements."
                }),
                "Schedule '" + schedule.Name + "': " + types.Count + " type(s) over " + total +
                " element row(s).");
        }

        var page = new List<ScheduleRow>(Math.Min(limit, Math.Max(0, total - offset)));
        for (var index = offset; index < total && page.Count < limit; index++)
            page.Add(rows[index]);

        // The composition summary answers "what is inside" without paging the
        // rows: a large result turns into a digest, and the digest keeps these
        // fields (S.45) — paging one row at a time was the observed failure.
        var byCategory = rows
            .GroupBy(row => row.Category ?? "(no category)")
            .Select(group => new { category = group.Key, count = group.Count() })
            .OrderByDescending(entry => entry.count)
            .ThenBy(entry => entry.category, StringComparer.Ordinal)
            .ToList();
        var uniqueTypes = rows
            .Where(row => row.TypeId.HasValue)
            .Select(row => row.TypeId!.Value)
            .Distinct()
            .Count();
        var sampleTypeNames = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.TypeName))
            .Select(row => row.TypeName!)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["scheduleId"] = schedule.Id.GetValue(),
            ["scheduleName"] = schedule.Name,
            ["isMulticategory"] = schedule.Definition is null
                                  || schedule.Definition.CategoryId is null
                                  || schedule.Definition.CategoryId == ElementId.InvalidElementId,
            ["total"] = total,
            ["returned"] = page.Count,
            ["offset"] = offset,
            ["truncated"] = offset + page.Count < total,
            ["nextOffset"] = offset + page.Count < total ? offset + page.Count : (int?)null,
            ["includeTypes"] = includeTypes,
            ["byCategory"] = byCategory,
            ["uniqueTypes"] = uniqueTypes,
            ["sampleTypeNames"] = sampleTypeNames,
            ["rows"] = page.Select(row => row.ToPayload()).ToList(),
            ["note"] = "Element ids come from the schedule view itself: the schedule filters " +
                       "and phase are already applied; byCategory/uniqueTypes describe the WHOLE " +
                       "set, not just this page. The displayed table text comes from " +
                       "get_schedule_data(scheduleId)."
        };

        var message = "Schedule '" + schedule.Name + "': " + page.Count + " of " + total +
                      " element row(s)";
        if (offset + page.Count < total)
            message += " (continue with offset " + (offset + page.Count) + ")";
        return ToolResult.Ok(AgentJson.Serialize(payload), message + ".");
    }

    private static List<ScheduleRow> CollectRows(
        Document doc,
        ViewSchedule schedule,
        bool includeTypes,
        CancellationToken ct)
    {
        var rows = new List<ScheduleRow>();
        var collector = new FilteredElementCollector(doc, schedule.Id);
        foreach (var element in collector)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= MaxCollected)
                break;

            var isType = element is ElementType;
            if (isType && !includeTypes)
                continue;

            rows.Add(ScheduleRow.From(doc, element, isType));
        }

        rows.Sort((left, right) => left.ElementIdValue.CompareTo(right.ElementIdValue));
        return rows;
    }

    private sealed class ScheduleRow
    {
        public long ElementIdValue { get; init; }

        public string Kind { get; init; } = "instance";

        public long? TypeId { get; init; }

        public string? TypeName { get; init; }

        public string? FamilyName { get; init; }

        public long? CategoryId { get; init; }

        public string? Category { get; init; }

        public string? Name { get; init; }

        internal static ScheduleRow From(Document doc, Element element, bool isType)
        {
            Element? typeElement = isType ? element : SafeType(doc, element);
            var symbol = typeElement as FamilySymbol;
            string? name = null;
            try
            {
                name = element.Name;
            }
            catch (Exception)
            {
                // Some view-specific elements do not expose a name.
            }

            return new ScheduleRow
            {
                ElementIdValue = element.Id.GetValue(),
                Kind = isType ? "type" : "instance",
                TypeId = typeElement?.Id.GetValue(),
                TypeName = typeElement?.Name,
                FamilyName = symbol?.Family?.Name,
                CategoryId = SafeCategoryId(element),
                Category = SafeCategoryName(element),
                Name = name
            };
        }

        internal object ToPayload() => new
        {
            elementId = ElementIdValue,
            kind = Kind,
            typeId = TypeId,
            typeName = TypeName,
            familyName = FamilyName,
            categoryId = CategoryId,
            category = Category,
            name = Name
        };

        private static Element? SafeType(Document doc, Element element)
        {
            try
            {
                var typeId = element.GetTypeId();
                return typeId is null || typeId == ElementId.InvalidElementId
                    ? null
                    : doc.GetElement(typeId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static long? SafeCategoryId(Element element)
        {
            try
            {
                return element.Category?.Id.GetValue();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? SafeCategoryName(Element element)
        {
            try
            {
                return element.Category?.Name;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
