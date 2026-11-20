using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class ListSchedulesTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public ListSchedulesTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_schedules",
        Description =
            "Lists schedules and specifications (ведомости, спецификации): name, category, visible " +
            "column headings, data row count, key schedule flag, sheet numbers. Filters: " +
            "categories, nameContains; internal keynote, titleblock revision and template schedules " +
            "are excluded unless requested. Paged: limit (default 200, max 500), offset, returned, " +
            "nextOffset, truncated. Unknown categories are reported in unknownCategories.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""categories"":{""type"":""array"",""items"":{""type"":""string""}},""nameContains"":{""type"":""string""},""includeTemplates"":{""type"":""boolean"",""default"":false},""includeInternal"":{""type"":""boolean"",""default"":false},""includeRevision"":{""type"":""boolean"",""default"":false},""fields"":{""type"":""array"",""description"":""Allowed: scheduleId,id,name,uniqueId,category|categoryName,fieldCount,hiddenFieldCount,fields,rowCount,isKeySchedule,isTemplate,sheetNumbers. Returned keys match the requested names."",""items"":{""type"":""string""}},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var categories = AgentJson.GetStringArray(args, "categories");
        var nameContains = AgentJson.GetString(args, "nameContains");
        var includeTemplates = AgentJson.GetBool(args, "includeTemplates", false);
        var includeInternal = AgentJson.GetBool(args, "includeInternal", false);
        var includeRevision = AgentJson.GetBool(args, "includeRevision", false);
        var fields = AgentJson.GetStringArray(args, "fields");
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = Math.Max(1, Math.Min(AgentJson.GetInt(args, "limit", DefaultLimit), MaxLimit));

        var unknownCategories = new List<string>();
        var categoryIds = AgentCategoryResolver.Resolve(doc, categories, unknownCategories);
        var categoryIdSet = new HashSet<long>();
        foreach (var id in categoryIds)
            categoryIdSet.Add(id.GetValue());

        // A requested filter that resolves to nothing must fail loudly. Silently
        // returning every schedule made the model report the full list as the
        // "schedules of walls".
        if (categories.Count > 0 && categoryIdSet.Count == 0)
        {
            var missed = unknownCategories.Count > 0 ? unknownCategories : categories;
            return ToolResult.Fail(
                "unknown_category",
                "None of the requested categories matched: " +
                string.Join(", ", missed) +
                AgentCategoryResolver.SuggestSuffix(doc, missed.First()) +
                ". [Action: use a category name from list_categories or an OST_* built-in name]");
        }

        var sheetNumbers = AgentScheduleReader.MapSheetNumbers(doc);
        var matched = new List<ScheduleCard>();
        foreach (var schedule in AgentScheduleReader.Collect(doc))
        {
            ct.ThrowIfCancellationRequested();

            if (schedule.IsTemplate && !includeTemplates)
                continue;
            if (schedule.IsInternalKeynoteSchedule && !includeInternal)
                continue;
            if (schedule.IsTitleblockRevisionSchedule && !includeRevision)
                continue;
            if (!MatchesName(schedule.Name, nameContains))
                continue;
            if (categoryIdSet.Count > 0 && !MatchesCategory(schedule, categoryIdSet))
                continue;

            matched.Add(AgentScheduleReader.CreateCard(doc, schedule, sheetNumbers));
        }

        matched.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        var total = matched.Count;
        var page = AgentPaging.Slice(matched, offset, limit);
        var hasMore = offset + page.Count < total;

        if (fields.Count > 0)
        {
            var projected = new List<JsonElement>(page.Count);
            var ignored = new List<string>();
            var substituted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var card in page)
            {
                projected.Add(AgentCardProjection.Apply(
                    AgentJson.SerializeCard(card), fields, ignored, substituted));
            }

            var available = page.Count > 0
                ? AgentCardProjection.AvailableFields(AgentJson.SerializeCard(page[0]))
                : Array.Empty<string>();

            var projectedPayload = new
            {
                total,
                offset,
                returned = projected.Count,
                nextOffset = hasMore ? offset + projected.Count : (int?)null,
                truncated = hasMore,
                unknownCategories = unknownCategories.Count > 0 ? unknownCategories : null,
                ignoredFields = ignored.Count > 0 ? ignored : null,
                substitutedFields = substituted.Count > 0 ? substituted : null,
                availableFields = ignored.Count > 0 ? available : null,
                schedules = projected
            };

            return ToolResult.Ok(
                AgentJson.Serialize(projectedPayload),
                "Matched " + total + " schedule(s); returned " + projected.Count + "." + PageHint(hasMore));
        }

        var payload = new
        {
            total,
            offset,
            returned = page.Count,
            nextOffset = hasMore ? offset + page.Count : (int?)null,
            truncated = hasMore,
            unknownCategories = unknownCategories.Count > 0 ? unknownCategories : null,
            schedules = page
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Matched " + total + " schedule(s); returned " + page.Count + "." + PageHint(hasMore));
    }

    private static string PageHint(bool hasMore) =>
        hasMore
            ? " The result is truncated — pass nextOffset as offset to read the next page."
            : string.Empty;

    private static bool MatchesName(string? value, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;
        if (string.IsNullOrEmpty(value))
            return false;

#if NET8_0_OR_GREATER
        return value.Contains(needle, StringComparison.OrdinalIgnoreCase);
#else
        return value!.IndexOf(needle!, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }

    private static bool MatchesCategory(ViewSchedule schedule, HashSet<long> categoryIds)
    {
        try
        {
            var categoryId = schedule.Definition.CategoryId;
            return categoryId is not null && categoryIds.Contains(categoryId.GetValue());
        }
        catch (Exception)
        {
            return false;
        }
    }
}