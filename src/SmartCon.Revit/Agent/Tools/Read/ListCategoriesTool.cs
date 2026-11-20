using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class ListCategoriesTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public ListCategoriesTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_categories",
        Description =
            "Lists model categories available in the project with element counts. " +
            "By default returns only categories that currently contain elements, " +
            "sorted by element count descending. Each row has categoryId (for list_filterable_parameters and categoryIds) and the OST_/display name. Use built-in names (OST_...) in queries.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""usedOnly"":{""type"":""boolean"",""default"":true},""limit"":{""type"":""integer"",""minimum"":1,""maximum"":500,""default"":200},""offset"":{""type"":""integer"",""minimum"":0,""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var usedOnly = AgentJson.GetBool(args, "usedOnly", true);
        var limit = Math.Max(1, Math.Min(AgentJson.GetInt(args, "limit", DefaultLimit), MaxLimit));
        var offset = Math.Max(0, AgentJson.GetInt(args, "offset", 0));

        var counts = new Dictionary<long, int>();
        if (usedOnly)
        {
            foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                ct.ThrowIfCancellationRequested();
                var category = element.Category;
                if (category is null)
                    continue;

                var id = category.Id.GetValue();
                counts.TryGetValue(id, out var count);
                counts[id] = count + 1;
            }
        }

        var categories = new List<CategoryCard>();
        foreach (Category category in doc.Settings.Categories)
        {
            ct.ThrowIfCancellationRequested();
            var id = category.Id.GetValue();
            counts.TryGetValue(id, out var count);
            if (usedOnly && count == 0)
                continue;

            categories.Add(new CategoryCard
            {
                CategoryId = id,
                Name = AgentElementCardFactory.GetCategoryName(category) ?? category.Name,
                DisplayName = category.Name,
                CategoryType = category.CategoryType.ToString(),
                ElementCount = count
            });
        }

        categories.Sort((left, right) =>
        {
            var byCount = right.ElementCount.CompareTo(left.ElementCount);
            return byCount != 0
                ? byCount
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        var total = categories.Count;
        var page = categories.Skip(offset).Take(limit).ToList();
        var nextOffset = offset + page.Count < total ? offset + page.Count : (int?)null;
        var payload = new
        {
            total,
            returned = page.Count,
            offset,
            limit,
            nextOffset,
            truncated = nextOffset is not null,
            categories = page
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Returned " + page.Count + " of " + total + " categor" +
            (total == 1 ? "y" : "ies") + ".");
    }
}
