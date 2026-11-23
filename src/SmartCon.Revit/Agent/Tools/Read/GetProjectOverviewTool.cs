using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Classification;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetProjectOverviewTool : AgentReadToolBase
{
    public GetProjectOverviewTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_project_overview",
        Description =
            "Returns a compact overview of the active Revit project: title, path, Revit version, " +
            "units, project information, level list, view/sheet counts, used categories with counts " +
            "and the state of the classification table (classification.loaded is false when no " +
            "classifier is connected to the document). " +
            "Use it first when you need to understand the document.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson.TrimEnd(',') + @"},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(level => level.Elevation)
            .Select(level => new LevelCard
            {
                LevelId = level.Id.GetValue(),
                Name = level.Name,
                ElevationMm = AgentUnitsFormat.ToMillimeters(level.Elevation),
                IsBuildingStory = level
                    .get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)
                    ?.AsInteger() == 1
            })
            .ToList();

        var views = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view => view.ViewType != ViewType.DrawingSheet
                           && view.ViewType != ViewType.Internal
                           && view.ViewType != ViewType.ProjectBrowser
                           && view.ViewType != ViewType.SystemBrowser
                           && view.ViewType != ViewType.Undefined
                           && !view.IsTemplate)
            .ToList();

        var sheetCount = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .GetElementCount();

        var categoryCounts = new Dictionary<long, int>();
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            ct.ThrowIfCancellationRequested();
            var category = element.Category;
            if (category is null)
                continue;

            var id = category.Id.GetValue();
            categoryCounts.TryGetValue(id, out var count);
            categoryCounts[id] = count + 1;
        }

        var categories = new List<CategoryCard>();
        foreach (Category category in doc.Settings.Categories)
        {
            var id = category.Id.GetValue();
            if (!categoryCounts.TryGetValue(id, out var count) || count == 0)
                continue;

            categories.Add(new CategoryCard
            {
                Name = AgentElementCardFactory.GetCategoryName(category) ?? category.Name,
                DisplayName = category.Name,
                ElementCount = count,
                CategoryType = category.CategoryType.ToString()
            });
        }

        categories.Sort((left, right) => right.ElementCount.CompareTo(left.ElementCount));

        var projectInfo = doc.ProjectInformation;
        var overview = new ProjectOverview
        {
            DocumentId = AgentDocumentIdentity.GetDocumentId(doc),
            Title = doc.Title,
            PathName = string.IsNullOrWhiteSpace(doc.PathName) ? null : doc.PathName,
            RevitVersion = AgentDocumentIdentity.GetRevitVersion(doc),
            IsWorkshared = doc.IsWorkshared,
            IsModified = doc.IsModified,
            ProjectNumber = projectInfo?.Number,
            ProjectName = projectInfo?.Name,
            Author = projectInfo?.Author,
            Units = AgentUnitsFormat.GetLengthUnitsLabel(doc),
            LevelCount = levels.Count,
            ViewCount = views.Count,
            SheetCount = sheetCount,
            Levels = levels,
            Categories = categories.Take(40).ToList()
        };

        return ToolResult.Ok(
            AgentJson.Serialize(WithClassification(overview, AgentClassificationReader.Read(doc))),
            "Project '" + doc.Title + "': " + levels.Count + " levels, " +
            views.Count + " views, " + sheetCount + " sheets.");
    }

    /// <summary>
    /// The overview plus its <c>classification</c> block. The block is appended
    /// to the serialized overview, so every other key keeps its exact shape.
    /// </summary>
    private static Dictionary<string, object?> WithClassification(
        ProjectOverview overview,
        AgentClassificationTable classification)
    {
        var payload = ToPayload(AgentJson.Serialize(overview));
        payload["classification"] = new
        {
            loaded = classification.Loaded,
            entries = classification.Entries.Count,
            source = classification.Source
        };
        return payload;
    }

    private static Dictionary<string, object?> ToPayload(JsonElement element)
    {
        var payload = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Null)
                payload[property.Name] = Convert(property.Value);
        }

        return payload;
    }

    private static object? Convert(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                return ToPayload(value);
            case JsonValueKind.Array:
                return value.EnumerateArray().Select(Convert).ToList();
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                return value.TryGetInt64(out var whole) ? whole : value.GetDouble();
            default:
                return value.GetBoolean();
        }
    }
}