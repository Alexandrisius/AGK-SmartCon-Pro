using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Pre-flight of the parameter × category pair (#400): which parameters a view
/// filter can actually test when all target categories are selected.
/// </summary>
public sealed class ListFilterableParametersTool : AgentReadToolBase
{
    private const int MaxCategories = 10;
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;
    private const int MaxSampleElements = 3;

    public ListFilterableParametersTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_filterable_parameters",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Lists the parameters a view filter can test for a set of categories. " +
            "categoryIds (or categories with OST_/localized names) is required; the answer is " +
            "the INTERSECTION of the filterable parameters of every category " +
            "(ParameterFilterUtilities.GetFilterableParametersInCommon), so " +
            "supportedByAllCategories=true means a rule by this parameter will be accepted for " +
            "the whole set. Each row: parameterId, name (localized as in the Revit UI), " +
            "storageType, isShared, supportedByAllCategories and missingFromCategories[]. Pass " +
            "parameterName to check one parameter before create_view_filter/set_filter_rules; " +
            "when it is missing for some categories the row reports them and the write tools " +
            "return parameter_not_supported_for_categories.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""categoryIds"":{""type"":""array"",""minItems"":1,""maxItems"":10,""items"":{""type"":""integer""}},""categories"":{""type"":""array"",""maxItems"":10,""description"":""OST_ names or localized category names; alias for categoryIds"",""items"":{""type"":""string""}},""parameterName"":{""type"":""string""},""nameContains"":{""type"":""string""},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var resolved = new List<ElementId>();
        var unknown = new List<string>();
        var missing = new List<long>();
        foreach (var categoryId in AgentJson.GetLongArray(args, "categoryIds"))
        {
            var category = Category.GetCategory(doc, ElementIdCompat.Create(categoryId));
            if (category is not null)
                resolved.Add(category.Id);
            else
                missing.Add(categoryId);
        }

        var categoryNames = AgentJson.GetStringArray(args, "categories");
        if (categoryNames.Count > 0)
        {
            var unknownFromNames = new List<string>();
            resolved.AddRange(AgentCategoryResolver.Resolve(doc, categoryNames, unknownFromNames));
            unknown.AddRange(unknownFromNames);
        }

        resolved = resolved
            .GroupBy(id => id.GetValue())
            .Select(group => group.First())
            .ToList();

        if (missing.Count > 0)
        {
            return ToolResult.Fail(
                "category_not_found",
                "No category with id(s) " + string.Join(", ", missing) +
                ". Use list_categories to see the ids and names.");
        }

        if (unknown.Count > 0)
        {
            return ToolResult.Fail(
                "unknown_categories",
                "Unknown categories: " + string.Join(", ", unknown) +
                ". Use list_categories to see the names.");
        }

        if (resolved.Count == 0)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "Pass categoryIds (integers from list_categories) or categories (OST_/localized " +
                "names); at least one filterable category is required.");
        }

        if (resolved.Count > MaxCategories)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "At most " + MaxCategories + " categories per call.");
        }

        var filterableCategories = FilterableCategoryIds();
        var notFilterable = new List<Dictionary<string, object?>>();
        foreach (var categoryId in resolved)
        {
            if (filterableCategories is null || filterableCategories.Contains(categoryId.GetValue()))
                continue;

            notFilterable.Add(new Dictionary<string, object?>
            {
                ["categoryId"] = categoryId.GetValue(),
                ["name"] = Category.GetCategory(doc, categoryId)?.Name
            });
        }

        // Per-category sets: the intersection is the answer, the per-category
        // difference tells which category blocks a parameter.
        var perCategory = new List<(ElementId Id, string? Name, HashSet<long> Supported)>();
        foreach (var categoryId in resolved)
        {
            ct.ThrowIfCancellationRequested();
            var supported = new HashSet<long>();
            foreach (var id in AgentFilterPreflight.FilterableInCommon(doc, new[] { categoryId }))
                supported.Add(id.GetValue());
            perCategory.Add((
                categoryId,
                Category.GetCategory(doc, categoryId)?.Name,
                supported));
        }

        var candidateIds = new List<long>();
        var seen = new HashSet<long>();
        foreach (var pair in perCategory)
        {
            foreach (var id in pair.Supported)
            {
                if (seen.Add(id))
                    candidateIds.Add(id);
            }
        }

        var lookupName = AgentJson.GetString(args, "parameterName");
        var nameContains = AgentJson.GetString(args, "nameContains");
        if (!string.IsNullOrWhiteSpace(lookupName))
        {
            var wanted = ResolveParameterByName(doc, lookupName!);
            if (wanted is null)
            {
                return ToolResult.Fail(
                    "parameter_not_found",
                    "Parameter '" + lookupName + "' does not exist in this document and is not " +
                    "a built-in parameter name. Use nameContains to browse the filterable list.");
            }

            candidateIds.RemoveAll(id => id != wanted.GetValue());
            if (!candidateIds.Contains(wanted.GetValue()))
                candidateIds.Add(wanted.GetValue());
        }

        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);

        var storage = ProbeStorageTypes(doc, resolved, ct);
        var rows = new List<(long Id, string? Name, bool Shared, StorageType? Storage)>();
        foreach (var id in candidateIds)
        {
            var name = AgentFilterPreflight.DescribeParameter(
                doc, ElementIdCompat.Create(id));
            if (!string.IsNullOrWhiteSpace(nameContains)
                && (name ?? string.Empty).IndexOf(
                    nameContains!, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            rows.Add((id, name, IsShared(doc, id), storage.TryGetValue(id, out var type)
                ? type
                : null));
        }

        rows.Sort((left, right) =>
            string.Compare(left.Name ?? string.Empty, right.Name ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));

        var parameters = new List<Dictionary<string, object?>>();
        for (var index = offset; index < rows.Count && parameters.Count < limit; index++)
        {
            var row = rows[index];
            var missingCategories = new List<Dictionary<string, object?>>();
            foreach (var pair in perCategory)
            {
                if (pair.Supported.Contains(row.Id))
                    continue;

                missingCategories.Add(new Dictionary<string, object?>
                {
                    ["id"] = pair.Id.GetValue(),
                    ["name"] = pair.Name
                });
            }

            parameters.Add(new Dictionary<string, object?>
            {
                ["parameterId"] = row.Id,
                ["name"] = row.Name,
                ["storageType"] = row.Storage?.ToString(),
                ["isShared"] = row.Shared,
                ["supportedByAllCategories"] = missingCategories.Count == 0,
                ["missingFromCategories"] = missingCategories
            });
        }

        var nextOffset = offset + parameters.Count;
        var supportedCount = rows.Count(row => IsSupportedEverywhere(perCategory, row.Id));
        var payload = new Dictionary<string, object?>
        {
            ["categories"] = perCategory
                .Select(pair => new { categoryId = pair.Id.GetValue(), name = pair.Name })
                .ToList(),
            ["parameters"] = parameters,
            ["total"] = rows.Count,
            ["returned"] = parameters.Count,
            ["offset"] = offset,
            ["nextOffset"] = nextOffset < rows.Count ? nextOffset : (int?)null,
            ["truncated"] = nextOffset < rows.Count,
            ["supportedByAllCategoriesCount"] = supportedCount,
            ["notFilterableCategories"] = notFilterable.Count > 0 ? notFilterable : null,
            ["rule"] = "create_view_filter/set_filter_rules accept a parameter only when it is " +
                       "supportedByAllCategories=true for every target category.",
            ["hint"] = "Read-back after a write: get_filter_details(includeOverrides=true) " +
                       "returns filterEnabled, visible and mode per applied view; " +
                       "get_view_settings/viewId or query_elements(viewId=...) show the result."
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Filterable parameters for " + perCategory.Count + " category(ies): " +
            supportedCount + " of " + rows.Count + " are supported by every category.");
    }

    private static bool IsSupportedEverywhere(
        IReadOnlyList<(ElementId Id, string? Name, HashSet<long> Supported)> perCategory,
        long parameterId)
    {
        foreach (var pair in perCategory)
        {
            if (!pair.Supported.Contains(parameterId))
                return false;
        }

        return true;
    }

    private static HashSet<long>? FilterableCategoryIds()
    {
        try
        {
            var result = new HashSet<long>();
            foreach (var id in ParameterFilterUtilities.GetAllFilterableCategories())
                result.Add(id.GetValue());
            return result;
        }
        catch (Exception)
        {
            // The check is advisory; the intersection itself stays authoritative.
            return null;
        }
    }

    private static ElementId? ResolveParameterByName(Document doc, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return null;

        if (Enum.TryParse<BuiltInParameter>(trimmed, ignoreCase: true, out var builtIn))
            return new ElementId(builtIn);

        foreach (var element in new FilteredElementCollector(doc)
                     .OfClass(typeof(ParameterElement)))
        {
            if (element is ParameterElement parameter
                && !string.IsNullOrWhiteSpace(parameter.Name)
                && string.Equals(parameter.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Id;
            }
        }

        foreach (BuiltInParameter candidate in Enum.GetValues(typeof(BuiltInParameter)))
        {
            try
            {
                if (!string.Equals(
                        LabelUtils.GetLabelFor(candidate),
                        trimmed,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            catch (Exception)
            {
                continue;
            }

            return new ElementId(candidate);
        }

        return null;
    }

    private static bool IsShared(Document doc, long parameterId)
    {
        if (parameterId < 0)
            return false;

        try
        {
            return doc.GetElement(ElementIdCompat.Create(parameterId))
                is SharedParameterElement;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Sampling a few elements per category is enough to learn the storage type
    /// of every parameter that occurs on them; unknown stays null instead of a
    /// guess.
    /// </summary>
    private static Dictionary<long, StorageType> ProbeStorageTypes(
        Document doc,
        IReadOnlyList<ElementId> categories,
        CancellationToken ct)
    {
        var result = new Dictionary<long, StorageType>();
        foreach (var categoryId in categories)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<Element> samples;
            try
            {
                samples = new FilteredElementCollector(doc)
                    .OfCategoryId(categoryId)
                    .WhereElementIsNotElementType()
                    .Take(MaxSampleElements);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var sample in samples)
            {
                Collect(sample);
                try
                {
                    var typeId = sample.GetTypeId();
                    if (typeId != ElementId.InvalidElementId
                        && doc.GetElement(typeId) is Element type)
                    {
                        Collect(type);
                    }
                }
                catch (Exception)
                {
                    // A sample without a readable type is skipped.
                }
            }

            // Type-only categories: the parameter may exist only on the type.
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

        return result;

        void Collect(Element owner)
        {
            try
            {
                foreach (Parameter parameter in owner.Parameters)
                {
                    var id = parameter.Id.GetValue();
                    if (!result.ContainsKey(id))
                        result[id] = parameter.StorageType;
                }
            }
            catch (Exception)
            {
                // A sample without readable parameters is skipped.
            }
        }
    }
}