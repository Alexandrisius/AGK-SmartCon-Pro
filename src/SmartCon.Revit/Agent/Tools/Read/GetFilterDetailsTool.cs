using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Full description of one parameter view filter: categories, the rule tree and
/// every view the filter is applied to.
/// </summary>
public sealed class GetFilterDetailsTool : AgentReadToolBase
{
    private const int MaxCategories = 50;
    private const int DefaultAppliedViewsLimit = 100;
    private const int MaxAppliedViewsLimit = 500;

    public GetFilterDetailsTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_filter_details",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Returns one parameter view filter by filterId or filterName: categories with names, " +
            "the rule tree (and/or/parameter rules with parameterId, parameterName, operator, value), " +
            "the parameters used and the views the filter is applied to (paged with " +
            "appliedViewsLimit/appliedViewsOffset, appliedViewsTotal shows the full count). " +
            "Numeric rules report value in Revit internal units plus unit=\"raw\" and " +
            "displayValue in project units; pass that value back with unit=\"raw\" when " +
            "restoring the rule. " +
            "includeOverrides=true adds per-view visibility and graphic overrides (what the " +
            "filter actually hides or colours). Each applied view reports both Revit UI " +
            "checkboxes: filterEnabled (View.GetIsFilterEnabled) and visible " +
            "(View.GetFilterVisibility), plus the mode they mean: hide = enabled and not " +
            "visible, show = enabled and visible, none = filter disabled. " +
            "includeAppliedViews=false skips the view scan " +
            "(appliedViewCount is then \"notComputed\"). Use list_filters to find filterId or " +
            "filterName first.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""filterId"":{""type"":""integer""},""filterName"":{""type"":""string""},""includeAppliedViews"":{""type"":""boolean"",""default"":true},""includeOverrides"":{""type"":""boolean"",""default"":false},""appliedViewsLimit"":{""type"":""integer"",""default"":100,""maximum"":500},""appliedViewsOffset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var filterId = AgentJson.GetLong(args, "filterId", 0);
        var filterName = AgentJson.GetString(args, "filterName");

        ParameterFilterElement? filter = null;
        if (filterId > 0)
            filter = AgentFilterWriteHelper.FindById(doc, filterId);
        if (filter is null && !string.IsNullOrWhiteSpace(filterName))
            filter = AgentFilterWriteHelper.FindByName(doc, filterName);

        if (filter is null)
        {
            return ToolResult.Fail(
                "filter_not_found",
                "Pass filterId or filterName of an existing view filter (see list_filters)." +
                (filterId > 0
                    ? " No view filter with id " + filterId + " exists."
                    : string.Empty));
        }

        var categories = new List<Dictionary<string, object?>>();
        List<ElementId> categoryIds;
        try
        {
            categoryIds = new List<ElementId>(filter.GetCategories());
        }
        catch (Exception)
        {
            categoryIds = new List<ElementId>();
        }

        foreach (var categoryId in categoryIds.Take(MaxCategories))
        {
            var category = Category.GetCategory(doc, categoryId);
            categories.Add(new Dictionary<string, object?>
            {
                ["categoryId"] = categoryId.GetValue(),
                ["name"] = category?.Name
            });
        }

        var rules = AgentFilterRuleDescriber.Describe(
            SafeElementFilter(filter), doc, SampleParameterSpecs(doc, categoryIds));
        var ruleCount = AgentFilterRuleDescriber.CountRules(SafeElementFilter(filter));

        var parameterIds = new List<long>();
        try
        {
            foreach (var parameterId in filter.GetElementFilterParameters())
                parameterIds.Add(parameterId.GetValue());
        }
        catch (Exception)
        {
            // Optional detail: some filters expose no parameters.
        }

        var includeAppliedViews = AgentJson.GetBool(args, "includeAppliedViews", true);
        var appliedViewsLimit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "appliedViewsLimit", DefaultAppliedViewsLimit),
            DefaultAppliedViewsLimit,
            MaxAppliedViewsLimit);
        var appliedViewsOffset = AgentPaging.NormalizeOffset(
            AgentJson.GetInt(args, "appliedViewsOffset", 0));

        var appliedViews = new List<Dictionary<string, object?>>();
        var appliedViewsTotal = 0;
        int? appliedViewsNextOffset = null;
        var usage = includeAppliedViews ? AgentFilterUsage.Map(doc, ct) : null;
        if (usage is not null && usage.TryGetValue(filter.Id.GetValue(), out var views))
        {
            appliedViewsTotal = views.Count;
            var includeOverrides = AgentJson.GetBool(args, "includeOverrides", false);
            for (var index = appliedViewsOffset;
                 index < views.Count && appliedViews.Count < appliedViewsLimit;
                 index++)
            {
                var view = views[index];
                var enabled = AgentFilterViewState.ReadEnabled(view, filter.Id);
                var visible = AgentFilterViewState.ReadVisible(view, filter.Id);
                var row = new Dictionary<string, object?>
                {
                    ["viewId"] = view.Id.GetValue(),
                    ["viewName"] = view.Name,
                    ["viewType"] = view.ViewType.ToString(),
                    // Two independent Revit UI checkboxes (#400): without
                    // filterEnabled a filter applied with visibility=none looks
                    // exactly like an enabled one.
                    ["filterEnabled"] = enabled,
                    ["visible"] = visible,
                    ["mode"] = AgentFilterViewState.ModeName(enabled, visible)
                };

                if (includeOverrides)
                    row["overrides"] = ReadOverrides(view, filter.Id);

                appliedViews.Add(row);
            }

            var nextOffset = appliedViewsOffset + appliedViews.Count;
            if (nextOffset < appliedViewsTotal)
                appliedViewsNextOffset = nextOffset;
        }

        var payload = new Dictionary<string, object?>
        {
            ["filterId"] = filter.Id.GetValue(),
            ["uniqueId"] = filter.UniqueId,
            ["name"] = filter.Name,
            ["categoryCount"] = categoryIds.Count,
            ["categories"] = categories,
            ["categoriesTruncated"] = categoryIds.Count > categories.Count,
            ["ruleCount"] = ruleCount,
            ["rules"] = rules,
            ["parameterIds"] = parameterIds,
            ["appliedViewCount"] = usage is null
                ? "notComputed"
                : usage.TryGetValue(filter.Id.GetValue(), out var count) ? count.Count : 0,
            ["appliedViewCountComputed"] = usage is not null,
            ["appliedViews"] = includeAppliedViews ? appliedViews : null,
            ["appliedViewsLimit"] = appliedViewsLimit,
            ["appliedViewsOffset"] = appliedViewsOffset,
            ["appliedViewsReturned"] = appliedViews.Count,
            ["appliedViewsTotal"] = usage is null ? "notComputed" : (object)appliedViewsTotal,
            ["appliedViewsNextOffset"] = appliedViewsNextOffset,
            ["appliedViewsTruncated"] = appliedViewsNextOffset is not null,
            ["hint"] = "Change the rules with set_filter_rules; remove the filter with " +
                       "delete_filter. Page the applied views with appliedViewsOffset."
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Read view filter '" + filter.Name + "': " + ruleCount + " rule(s), " +
            categoryIds.Count + " category(ies).");
    }

    private static ViewOverrideState? ReadOverrides(
        View view,
        ElementId filterId)
    {
        try
        {
            return AgentFilterWriteHelper.Describe(view.GetFilterOverrides(filterId));
        }
        catch (Exception)
        {
            return null;
        }
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

    /// <summary>
    /// Spec per parameter id from the filter's categories: instance parameters, the
    /// element type (type parameters live there, not on the instance) and project /
    /// shared ParameterElements. Numeric rules store internal units, and the spec is
    /// what turns them into a display value (B1).
    /// </summary>
    private static Dictionary<long, ForgeTypeId> SampleParameterSpecs(
        Document doc,
        IReadOnlyList<ElementId> categoryIds)
    {
        var map = new Dictionary<long, ForgeTypeId>();
        foreach (var categoryId in categoryIds)
        {
            Element? sample;
            try
            {
                sample = new FilteredElementCollector(doc)
                    .OfCategoryId(categoryId)
                    .WhereElementIsNotElementType()
                    .FirstElement();
            }
            catch (Exception)
            {
                continue;
            }

            if (sample is not null)
            {
                Collect(sample);
                var type = doc.GetElement(sample.GetTypeId());
                if (type is not null)
                    Collect(type);
            }

            // Type-only categories (doors/windows not placed yet) still carry specs.
            try
            {
                var typeOnly = new FilteredElementCollector(doc)
                    .OfCategoryId(categoryId)
                    .WhereElementIsElementType()
                    .FirstElement();
                if (typeOnly is not null)
                    Collect(typeOnly);
            }
            catch (Exception)
            {
                // A category whose types cannot be collected is skipped.
            }
        }

        foreach (var parameterId in map.Keys.ToList())
        {
            if (parameterId <= 0 || doc.GetElement(ElementIdCompat.Create(parameterId))
                    is not ParameterElement element)
            {
                continue;
            }

            try
            {
                var spec = element.GetDefinition()?.GetDataType();
                if (spec is not null)
                    map[parameterId] = spec;
            }
            catch (Exception)
            {
                // A definition without a spec is skipped.
            }
        }

        return map;

        void Collect(Element owner)
        {
            try
            {
                foreach (Parameter parameter in owner.Parameters)
                {
                    var spec = parameter.Definition?.GetDataType();
                    if (spec is not null)
                        map[parameter.Id.GetValue()] = spec;
                }
            }
            catch (Exception)
            {
                // A sample that rejects enumeration is skipped.
            }
        }
    }
}