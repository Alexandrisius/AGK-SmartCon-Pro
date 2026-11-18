using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

public sealed class AgentReadToolsTests : RevitApiTest
{
    private const string SeededLevelName = "SC_Agent_Level";
    private const string SeededViewName = "SC_Agent_Plan";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private bool _wallCreated;
    private long _wallId;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _transactions.RunInTransaction(Doc, "Seed agent model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = SeededLevelName;
            _ = ModelSeed.CreateViewPlanChecked(doc, level.Id, SeededViewName);

            var wall = ModelSeed.TryCreateWall(doc, level.Id);
            _wallCreated = wall is not null;
            _wallId = wall?.Id.GetValue() ?? 0;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task ProjectOverview_ReturnsSeededLevelAndViewCounts()
    {
        var tool = new GetProjectOverviewTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(Doc, EmptyArgs(), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data).IsNotNull();

        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("title").GetString()).IsNotNull();
        await Assert.That(data.GetProperty("levelCount").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(data.GetProperty("viewCount").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(data.GetProperty("levels").GetRawText()).Contains(SeededLevelName);
    }

    [Test]
    public async Task ListLevels_ContainsSeededLevelWithElevation()
    {
        var tool = new ListLevelsTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(Doc, EmptyArgs(), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var levels = result.Data!.Value.GetProperty("levels");
        var seeded = levels.EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("name").GetString() == SeededLevelName);

        await Assert.That(seeded.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(seeded.GetProperty("elevationMm").GetDouble()).IsEqualTo(914.4).Within(0.5);
    }

    [Test]
    public async Task ListViews_FiltersByTypeAndName()
    {
        var tool = new ListViewsTool(new StubRevitContext(Doc));
        var args = JsonDocument.Parse(
            "{\"types\":[\"FloorPlan\"],\"nameContains\":\"SC_Agent\"}").RootElement.Clone();

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var views = result.Data!.Value.GetProperty("views");
        var seeded = views.EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("name").GetString() == SeededViewName);

        await Assert.That(seeded.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(seeded.GetProperty("viewType").GetString()).IsEqualTo("FloorPlan");
        await Assert.That(seeded.GetProperty("levelName").GetString()).IsEqualTo(SeededLevelName);
    }

    [Test]
    public async Task ListCategories_ContainsSeededWallCategory()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены — сидирование категории невозможно");

        var tool = new ListCategoriesTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(Doc, EmptyArgs(), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var categories = result.Data!.Value.GetProperty("categories");
        var walls = categories.EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("name").GetString() == "OST_Walls");

        await Assert.That(walls.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(walls.GetProperty("elementCount").GetInt32()).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task GetSelectedElements_WithoutUiSelection_ReturnsStructuredFailure()
    {
        var tool = new GetSelectedElementsTool(new StubRevitContext(Doc), new NoSelectionProvider());

        var result = tool.ExecuteForTests(Doc, EmptyArgs(), CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsNotNull();
    }

    [Test]
    public async Task ProjectContextProvider_BuildDocument_ReturnsCountsAndDocumentId()
    {
        var context = AgentProjectContextProvider.BuildDocument(Doc, CancellationToken.None);

        await Assert.That(context.HasDocument).IsTrue();
        await Assert.That(context.DocumentId).IsNotEmpty();
        await Assert.That(context.LevelCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(context.ViewCount).IsGreaterThanOrEqualTo(1);
        await Assert.That(context.Units).IsNotEmpty();
    }

    [Test]
    public async Task ElementCardFactory_CreatesCardWithCategoryAndLevel()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены — карточку элемента не построить");

        var wall = Doc.GetElement(new ElementId(_wallId));
        await Assert.That(wall).IsNotNull();

        var card = AgentElementCardFactory.Create(Doc, wall!);

        await Assert.That(card.ElementId).IsEqualTo(_wallId);
        await Assert.That(card.Category).IsEqualTo("OST_Walls");
        await Assert.That(card.LevelName).IsEqualTo(SeededLevelName);
        await Assert.That(card.UniqueId).IsNotEmpty();
    }

    [Test]
    public async Task ListLevels_TotalMatchesProjectOverviewLevelCount()
    {
        var overview = new GetProjectOverviewTool(new StubRevitContext(Doc))
            .ExecuteForTests(Doc, EmptyArgs(), CancellationToken.None);
        var levels = new ListLevelsTool(new StubRevitContext(Doc))
            .ExecuteForTests(Doc, EmptyArgs(), CancellationToken.None);

        await Assert.That(overview.Success).IsTrue();
        await Assert.That(levels.Success).IsTrue();
        await Assert.That(levels.Data!.Value.GetProperty("total").GetInt32())
            .IsEqualTo(overview.Data!.Value.GetProperty("levelCount").GetInt32());
    }

    [Test]
    public async Task QueryElements_ContainsByTypeName_ForElementIdParameter()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены — запрос по типу невозможен");

        var wall = Doc.GetElement(new ElementId(_wallId));
        var type = Doc.GetElement(wall!.GetTypeId());
        var typeName = type?.Name;
        if (string.IsNullOrWhiteSpace(typeName))
            Skip.Test("У стены нет имени типа");

        var needle = typeName!.Length <= 4 ? typeName : typeName.Substring(0, 4);
        var tool = new QueryElementsTool(new StubRevitContext(Doc));
        var args = JsonDocument.Parse(
            "{\"categories\":[\"OST_Walls\"],\"conditions\":[{\"parameter\":\"ELEM_TYPE_PARAM\"," +
            "\"op\":\"contains\",\"value\":\"" + needle.Replace("\"", "\\\"") + "\"}],\"limit\":50}")
            .RootElement.Clone();

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("total").GetInt32()).IsGreaterThanOrEqualTo(1);
        var ids = result.Data.Value.GetProperty("elements").EnumerateArray()
            .Select(card => card.GetProperty("elementId").GetInt64())
            .ToList();
        await Assert.That(ids).Contains(_wallId);
    }

    [Test]
    public async Task GetViewSettings_WithoutFields_ReturnsCompactSummary()
    {
        var tool = new GetViewSettingsTool(new StubRevitContext(Doc));
        var args = JsonDocument.Parse("{\"viewId\":" + GetSeededViewId() + "}").RootElement.Clone();

        var summary = tool.ExecuteForTests(Doc, args, CancellationToken.None);
        var full = tool.ExecuteForTests(
            Doc,
            JsonDocument.Parse(
                "{\"viewId\":" + GetSeededViewId() + ",\"compact\":false}").RootElement.Clone(),
            CancellationToken.None);

        await Assert.That(summary.Success).IsTrue();
        await Assert.That(full.Success).IsTrue();
        await Assert.That(summary.Data!.Value.TryGetProperty("hiddenCategoryCount", out _)).IsTrue();
        await Assert.That(summary.Data.Value.TryGetProperty("hiddenCategories", out _)).IsFalse();
        await Assert.That(summary.Data.Value.GetRawText().Length)
            .IsLessThan(full.Data!.Value.GetRawText().Length);
    }

    [Test]
    public async Task GetViewSettings_WithFields_ReturnsOnlyRequestedFields()
    {
        var tool = new GetViewSettingsTool(new StubRevitContext(Doc));
        var args = JsonDocument.Parse(
            "{\"viewId\":" + GetSeededViewId() + ",\"fields\":[\"scale\",\"viewRange\"]}")
            .RootElement.Clone();

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.TryGetProperty("scale", out _)).IsTrue();
        await Assert.That(result.Data.Value.TryGetProperty("viewRange", out _)).IsTrue();
        await Assert.That(result.Data.Value.TryGetProperty("hiddenCategories", out _)).IsFalse();
        await Assert.That(result.Data.Value.TryGetProperty("uniqueId", out _)).IsFalse();
    }

    [Test]
    public async Task GetViewTemplateDetails_DefaultCompact_AndFieldsProjection()
    {
        var templateId = CreateSeededTemplate();
        var tool = new GetViewTemplateDetailsTool(new StubRevitContext(Doc));

        var summary = tool.ExecuteForTests(
            Doc,
            JsonDocument.Parse("{\"templateId\":" + templateId + "}").RootElement.Clone(),
            CancellationToken.None);
        var projected = tool.ExecuteForTests(
            Doc,
            JsonDocument.Parse(
                "{\"templateId\":" + templateId + ",\"fields\":[\"viewRange\",\"scale\"]}")
                .RootElement.Clone(),
            CancellationToken.None);
        var full = tool.ExecuteForTests(
            Doc,
            JsonDocument.Parse(
                "{\"templateId\":" + templateId + ",\"compact\":false}").RootElement.Clone(),
            CancellationToken.None);

        await Assert.That(summary.Success).IsTrue();
        await Assert.That(projected.Success).IsTrue();
        await Assert.That(full.Success).IsTrue();

        var summaryPayload = summary.Data!.Value;
        await Assert.That(summaryPayload.GetProperty("settings")
            .TryGetProperty("hiddenCategoryCount", out _)).IsTrue();
        await Assert.That(summaryPayload.GetProperty("settings")
            .TryGetProperty("hiddenCategories", out _)).IsFalse();

        var projectedSettings = projected.Data!.Value.GetProperty("settings");
        await Assert.That(projectedSettings.TryGetProperty("viewRange", out _)).IsTrue();
        await Assert.That(projectedSettings.TryGetProperty("hiddenCategories", out _)).IsFalse();

        await Assert.That(summaryPayload.GetRawText().Length)
            .IsLessThan(full.Data!.Value.GetRawText().Length);
    }

    private long GetSeededViewId()
    {
        return new FilteredElementCollector(Doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .First(view => view.Name == SeededViewName)
            .Id.GetValue();
    }

    private long CreateSeededTemplate()
    {
        long templateId = 0;
        _transactions!.RunInTransaction(Doc, "Seed agent template", doc =>
        {
            var plan = (ViewPlan)doc.GetElement(ElementIdCompat.Create(GetSeededViewId()));
            var template = plan.CreateViewTemplate();
            template.Name = "SC_Agent_Template";
            templateId = template.Id.GetValue();
        });

        return templateId;
    }

    private static JsonElement EmptyArgs()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private sealed class NoSelectionProvider : IAgentSelectionProvider
    {
        public IReadOnlyList<ElementId>? GetSelectedElementIds() => null;
    }
}