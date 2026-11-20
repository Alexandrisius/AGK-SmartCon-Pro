using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the parameter view filters of the project with their categories, rule
/// count and how many views use each filter. Read-only counterpart of
/// create_view_filter / set_filter_rules / delete_filter.
/// </summary>
public sealed class ListFiltersTool : AgentReadToolBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;
    private const int MaxCategoryNames = 10;

    public ListFiltersTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_filters",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Lists parameter view filters (ParameterFilterElement): filterId, uniqueId, name, " +
            "category count with the first category names, ruleCount, rulesDigest/rulesSummary and " +
            "appliedViewCount. rulesDigest groups filters with the same rules and categories; " +
            "duplicateGroups lists those groups (disable with includeDuplicateGroups=false). " +
            "Optional nameContains and viewId narrow the list; viewId returns only the filters " +
            "applied to that view. includeAppliedViews=false skips the view scan (appliedViewCount " +
            "is then \"notComputed\"). Use get_filter_details for the rule tree and the applied views.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""nameContains"":{""type"":""string""},""viewId"":{""type"":""integer""},""includeAppliedViews"":{""type"":""boolean"",""default"":true},""includeDuplicateGroups"":{""type"":""boolean"",""default"":true},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var nameContains = AgentJson.GetString(args, "nameContains");
        var viewId = AgentJson.GetLong(args, "viewId", 0);
        var includeAppliedViews = AgentJson.GetBool(args, "includeAppliedViews", true);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);

        var filters = new FilteredElementCollector(doc)
            .OfClass(typeof(ParameterFilterElement))
            .Cast<ParameterFilterElement>()
            .Where(filter => string.IsNullOrWhiteSpace(nameContains)
                             || filter.Name.IndexOf(
                                 nameContains!,
                                 StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(filter => filter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        long? onlyViewId = null;
        if (viewId > 0)
        {
            if (doc.GetElement(ElementIdCompat.Create(viewId)) is not View view)
            {
                return ToolResult.Fail(
                    "view_not_found",
                    "No view with id " + viewId + " exists in this document.");
            }

            if (!AgentFilterUsage.SupportsFilters(view))
            {
                return ToolResult.Fail(
                    "unsupported_view_type",
                    "View '" + view.Name + "' (" + view.ViewType + ") has no " +
                    "Visibility/Graphics filters, so viewId cannot narrow the list. Omit viewId " +
                    "or pass a plan/section/3D view.");
            }

            var applied = AgentFilterUsage.FiltersOf(view);
            filters = filters.Where(filter => applied.Contains(filter.Id.GetValue())).ToList();
            onlyViewId = viewId;
        }

        var usage = includeAppliedViews
            ? AgentFilterUsage.Map(doc, onlyViewId, ct)
            : null;

        // Rules are read once per filter: the digest lets the model spot duplicates
        // without opening all 47 filters one by one (external benchmark, task 2).
        var rows = new List<FilterRow>(filters.Count);
        foreach (var filter in filters)
        {
            var elementFilter = SafeElementFilter(filter);
            var categoryIds = SafeCategories(filter);
            rows.Add(new FilterRow
            {
                Filter = filter,
                CategoryIds = categoryIds,
                RuleCount = AgentFilterRuleDescriber.CountRules(elementFilter),
                Digest = AgentFilterRuleDescriber.Digest(elementFilter, categoryIds),
                Summary = AgentFilterRuleDescriber.Summary(elementFilter, doc)
            });
        }

        var includeDuplicateGroups = AgentJson.GetBool(args, "includeDuplicateGroups", true);
        var duplicateGroups = includeDuplicateGroups
            ? BuildDuplicateGroups(rows)
            : new List<Dictionary<string, object?>>();

        var page = new List<Dictionary<string, object?>>();
        for (var index = offset; index < rows.Count && page.Count < limit; index++)
        {
            var row = rows[index];
            var categoryNames = new List<string>();
            foreach (var categoryId in row.CategoryIds.Take(MaxCategoryNames))
            {
                var category = Category.GetCategory(doc, categoryId);
                categoryNames.Add(category?.Name ?? categoryId.GetValue().ToString());
            }

            page.Add(new Dictionary<string, object?>
            {
                ["filterId"] = row.Filter.Id.GetValue(),
                ["uniqueId"] = row.Filter.UniqueId,
                ["name"] = row.Filter.Name,
                ["categoryCount"] = row.CategoryIds.Count,
                ["categories"] = categoryNames,
                ["categoriesTruncated"] = row.CategoryIds.Count > categoryNames.Count,
                ["ruleCount"] = row.RuleCount,
                ["rulesDigest"] = row.Digest,
                ["rulesSummary"] = row.Summary,
                ["appliedViewCount"] = usage is null
                    ? "notComputed"
                    : AgentFilterUsage.Count(usage, row.Filter.Id.GetValue())
            });
        }

        var nextOffset = offset + page.Count;
        var payload = new Dictionary<string, object?>
        {
            ["filters"] = page,
            ["total"] = rows.Count,
            ["returned"] = page.Count,
            ["offset"] = offset,
            ["nextOffset"] = nextOffset < rows.Count ? nextOffset : (int?)null,
            ["truncated"] = nextOffset < rows.Count,
            ["viewId"] = viewId > 0 ? viewId : (long?)null,
            ["narrowedByView"] = viewId > 0,
            ["appliedViewCountComputed"] = usage is not null,
            ["duplicateGroups"] = duplicateGroups,
            ["duplicateGroupCount"] = duplicateGroups.Count,
            ["hint"] = "rulesDigest groups filters with the same rules and categories; " +
                       "duplicateGroups lists them. Use get_filter_details(filterId) for the " +
                       "full rule tree, applied views and overrides."
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            filters.Count == 0
                ? "No parameter view filters match the request."
                : "Listed " + page.Count + " of " + rows.Count + " view filter(s)" +
                  (duplicateGroups.Count == 0
                      ? "."
                      : "; " + duplicateGroups.Count + " duplicate group(s) found."));
    }

    private static List<Dictionary<string, object?>> BuildDuplicateGroups(
        IReadOnlyList<FilterRow> rows)
    {
        var byDigest = new Dictionary<string, List<FilterRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Digest == "unavailable")
                continue;

            if (!byDigest.TryGetValue(row.Digest, out var group))
            {
                group = new List<FilterRow>();
                byDigest[row.Digest] = group;
            }

            group.Add(row);
        }

        var result = new List<Dictionary<string, object?>>();
        foreach (var pair in byDigest
                     .Where(pair => pair.Value.Count > 1)
                     .OrderByDescending(pair => pair.Value.Count)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var filters = new List<Dictionary<string, object?>>();
            foreach (var row in pair.Value)
            {
                filters.Add(new Dictionary<string, object?>
                {
                    ["filterId"] = row.Filter.Id.GetValue(),
                    ["name"] = row.Filter.Name,
                    ["ruleCount"] = row.RuleCount
                });
            }

            result.Add(new Dictionary<string, object?>
            {
                ["digest"] = pair.Key,
                ["count"] = pair.Value.Count,
                ["filters"] = filters
            });
        }

        return result;
    }

    private static ElementFilter? SafeElementFilter(ParameterFilterElement filter)
    {
        try
        {
            return filter.GetElementFilter();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<ElementId> SafeCategories(ParameterFilterElement filter)
    {
        try
        {
            return new List<ElementId>(filter.GetCategories());
        }
        catch (Exception)
        {
            return new List<ElementId>();
        }
    }

    private sealed class FilterRow
    {
        public required ParameterFilterElement Filter { get; init; }

        public required List<ElementId> CategoryIds { get; init; }

        public int RuleCount { get; init; }

        public required string Digest { get; init; }

        public required string Summary { get; init; }
    }
}