using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Write;

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
            "applied to that view AND their effect there (filterEnabled, visible, mode: " +
            "hide/show/none), which is the compact answer to 'is this filter on this view and " +
            "what does it do'. Rules are then omitted unless includeRules=true. " +
            "includeAppliedViews=false skips the view scan (appliedViewCount is then " +
            "\"notComputed\"). Use get_filter_details for the full rule tree of one filter.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""nameContains"":{""type"":""string""},""viewId"":{""type"":""integer""},""includeAppliedViews"":{""type"":""boolean"",""default"":true},""includeDuplicateGroups"":{""type"":""boolean"",""default"":true},""includeRules"":{""type"":""boolean"",""description"":""Default true without viewId, false with viewId""},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var nameContains = AgentJson.GetString(args, "nameContains");
        var viewId = AgentJson.GetLong(args, "viewId", 0);
        var includeAppliedViews = AgentJson.GetBool(args, "includeAppliedViews", true);
        // Narrowed by view, the caller asks "what does this filter do here", not
        // "what are its rules": the rule digests of 47 filters cost ~15k tokens
        // (external benchmark, task 4).
        var includeRules = AgentJson.GetBool(args, "includeRules", viewId <= 0);
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
        View? narrowedView = null;
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
            narrowedView = view;
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
                Digest = includeRules
                    ? AgentFilterRuleDescriber.Digest(elementFilter, categoryIds)
                    : null,
                Summary = includeRules
                    ? AgentFilterRuleDescriber.Summary(elementFilter, doc)
                    : null
            });
        }

        var includeDuplicateGroups = includeRules
                                    && AgentJson.GetBool(args, "includeDuplicateGroups", true);
        var duplicateGroups = includeDuplicateGroups
            ? BuildDuplicateGroups(rows, usage)
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

            var entry = new Dictionary<string, object?>
            {
                ["filterId"] = row.Filter.Id.GetValue(),
                ["uniqueId"] = row.Filter.UniqueId,
                ["name"] = row.Filter.Name,
                ["categoryCount"] = row.CategoryIds.Count,
                ["categories"] = categoryNames,
                ["categoriesTruncated"] = row.CategoryIds.Count > categoryNames.Count,
                ["ruleCount"] = row.RuleCount,
                ["appliedViewCount"] = usage is null
                    ? "notComputed"
                    : AgentFilterUsage.Count(usage, row.Filter.Id.GetValue())
            };

            if (includeRules)
            {
                entry["rulesDigest"] = row.Digest;
                entry["rulesSummary"] = row.Summary;
            }

            if (viewId > 0)
            {
                // Two Revit checkboxes, one readable verdict (#400): a filter can be
                // applied but disabled, or enabled with all categories shown.
                var enabled = AgentFilterViewState.ReadEnabled(narrowedView!, row.Filter.Id);
                var visible = AgentFilterViewState.ReadVisible(narrowedView!, row.Filter.Id);
                entry["filterEnabled"] = enabled;
                entry["visible"] = visible;
                entry["mode"] = AgentFilterViewState.ModeName(enabled, visible);
            }

            page.Add(entry);
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
            ["includeRules"] = includeRules,
            ["duplicateGroups"] = includeRules ? duplicateGroups : null,
            ["duplicateGroupCount"] = includeRules ? duplicateGroups.Count : "notComputed",
            ["hint"] = includeRules
                ? "rulesDigest groups filters with the same rules and categories; " +
                  "duplicateGroups lists them. Use get_filter_details(filterId) for the " +
                  "full rule tree, applied views and overrides."
                : viewId > 0
                    ? "Rules were skipped because viewId narrows the question to filter effects. " +
                      "Pass includeRules=true for rulesDigest/rulesSummary, or " +
                      "get_filter_details(filterId) for one filter."
                    : "Rules were skipped as requested. Pass includeRules=true for " +
                      "rulesDigest/rulesSummary and duplicateGroups."
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
        IReadOnlyList<FilterRow> rows,
        Dictionary<long, List<View>>? usage)
    {
        var byDigest = new Dictionary<string, List<FilterRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            // net48 has no [NotNullWhen(false)] on string.IsNullOrEmpty, so the
            // nullable narrowing must be spelled as a pattern.
            if (row.Digest is not { Length: > 0 } digest)
                continue;

            if (!byDigest.TryGetValue(digest, out var group))
            {
                group = new List<FilterRow>();
                byDigest[digest] = group;
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
                // Liveness of every copy (S.27): a group is resolved by one
                // merge_duplicate_filters call when it is clear which copy is alive.
                filters.Add(new Dictionary<string, object?>
                {
                    ["filterId"] = row.Filter.Id.GetValue(),
                    ["name"] = row.Filter.Name,
                    ["ruleCount"] = row.RuleCount,
                    ["appliedViewCount"] = usage is null
                        ? "notComputed"
                        : AgentFilterUsage.Count(usage, row.Filter.Id.GetValue())
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

        public string? Digest { get; init; }

        public string? Summary { get; init; }
    }
}