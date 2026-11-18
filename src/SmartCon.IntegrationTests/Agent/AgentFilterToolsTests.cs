using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

public sealed class AgentFilterToolsTests : RevitApiTest
{
    private const string LevelName = "SC_FilterLevel";
    private const string PlanName = "SC_FilterPlan";
    private const string WallComment = "SC_Filter_Wall";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _levelId;
    private long _viewId;
    private bool _wallsCreated;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed filter model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = LevelName;
            _levelId = level.Id.GetValue();

            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanName);
            _viewId = plan.Id.GetValue();

            for (var index = 0; index < 3; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, level.Id, index * 3.0);
                if (wall is null)
                    continue;

                wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                    ?.Set(WallComment);
                _wallsCreated = true;
            }
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task CreateViewFilter_DryRun_DoesNotCreateAnything()
    {
        if (!_wallsCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"categories\":[\"OST_Walls\"]," +
            "\"conditions\":[{\"parameter\":\"ALL_MODEL_INSTANCE_COMMENTS\",\"op\":\"eq\"," +
            "\"value\":\"" + WallComment + "\"}],\"visibility\":\"hide\",\"dryRun\":true}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("dryRun").GetBoolean()).IsTrue();
        await Assert.That(result.Data.Value.GetProperty("created").GetBoolean()).IsTrue();
        await Assert.That(result.Data.Value.GetProperty("sections").GetArrayLength())
            .IsGreaterThanOrEqualTo(3);
        await Assert.That(CountFilters()).IsEqualTo(0);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CreateViewFilter_Apply_CreatesFilterAndHidesMatchingElements()
    {
        if (!_wallsCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"categories\":[\"OST_Walls\"]," +
            "\"conditions\":[{\"parameter\":\"ALL_MODEL_INSTANCE_COMMENTS\",\"op\":\"eq\"," +
            "\"value\":\"" + WallComment + "\"}],\"visibility\":\"hide\",\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var filterId = data.GetProperty("filterId").GetInt64();
        await Assert.That(data.GetProperty("created").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(1);

        var view = (View)Doc.GetElement(ElementIdCompat.Create(_viewId));
        await Assert.That(view.GetFilters().Count).IsEqualTo(1);
        await Assert.That(view.GetFilterVisibility(ElementIdCompat.Create(filterId))).IsFalse();

        var readBack = data.GetProperty("readBack");
        await Assert.That(readBack.GetArrayLength()).IsEqualTo(1);
        await Assert.That(readBack[0].GetProperty("visible").GetBoolean()).IsFalse();

        await Assert.That(_changeSets!.All.Count).IsEqualTo(1);
        var operations = _changeSets.Last!.Items
            .Select(item => item.ParameterName ?? string.Empty)
            .ToList();
        await Assert.That(operations).Contains("filter_created");
        await Assert.That(operations).Contains("filter");
    }

    [Test]
    public async Task CreateViewFilter_SecondApply_IsNoOp()
    {
        if (!_wallsCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"categories\":[\"OST_Walls\"]," +
            "\"conditions\":[{\"parameter\":\"ALL_MODEL_INSTANCE_COMMENTS\",\"op\":\"eq\"," +
            "\"value\":\"" + WallComment + "\"}],\"visibility\":\"hide\",\"dryRun\":false}");

        var first = tool.ExecuteForTests(args, approved: true, CancellationToken.None);
        var second = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(first.Success).IsTrue();
        await Assert.That(second.Success).IsTrue();
        var data = second.Data!.Value;
        await Assert.That(data.GetProperty("created").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("reused").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(0);
        await Assert.That(data.GetProperty("skipped").GetArrayLength()).IsEqualTo(1);
        await Assert.That(CountFilters()).IsEqualTo(1);
    }

    [Test]
    public async Task CreateViewFilter_CategoryOnly_CreatesFilterWithoutRules()
    {
        var localized = Category.GetCategory(Doc, BuiltInCategory.OST_Walls)?.Name;
        if (string.IsNullOrWhiteSpace(localized))
            Skip.Test("Категория стен недоступна в документе");

        var tool = CreateTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"categories\":[\"" + localized + "\"]," +
            "\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("filterId").GetInt64())
            .IsGreaterThan(0);
        await Assert.That(CountFilters()).IsEqualTo(1);
    }

    [Test]
    public async Task CreateViewFilter_LevelCondition_UsesElementIdRule()
    {
        var tool = CreateTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"categories\":[\"OST_Walls\"]," +
            "\"conditions\":[{\"field\":\"level\",\"op\":\"eq\",\"value\":\"" + LevelName + "\"}]," +
            "\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(CountFilters()).IsEqualTo(1);
    }

    [Test]
    public async Task CreateViewFilter_UnknownCategory_ReturnsUnknownList()
    {
        var tool = CreateTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"categories\":[\"OST_NoSuchCategory\"]}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unknown_categories");
        await Assert.That(result.Data!.Value.GetProperty("unknownCategories")
            .GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task CreateViewFilter_UnknownParameter_ListsAvailableParameters()
    {
        var tool = CreateTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"categories\":[\"OST_Walls\"]," +
            "\"conditions\":[{\"parameter\":\"НетТакогоПараметра\",\"op\":\"eq\",\"value\":\"x\"}]}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("parameter_not_found");
        await Assert.That(result.Data!.Value.GetProperty("availableParameters")
            .GetArrayLength()).IsGreaterThan(0);
    }

    [Test]
    public async Task SetFilterOverrides_Apply_SetsColourAndTransparency()
    {
        var filterId = CreateFilterElement("SC_Filter_Overrides");

        var tool = CreateOverridesTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"filterId\":" + filterId + "," +
            "\"color\":\"#FF0000\",\"transparency\":30,\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var readBack = result.Data!.Value.GetProperty("readBack");
        await Assert.That(readBack.GetArrayLength()).IsEqualTo(1);
        var overrides = readBack[0].GetProperty("overrides");
        await Assert.That(overrides.GetProperty("surfaceForegroundColor").GetString())
            .IsEqualTo("#FF0000");
        await Assert.That(overrides.GetProperty("transparency").GetInt32()).IsEqualTo(30);

        var view = (View)Doc.GetElement(ElementIdCompat.Create(_viewId));
        var settings = view.GetFilterOverrides(ElementIdCompat.Create(filterId));
        await Assert.That(settings.Transparency).IsEqualTo(30);
    }

    [Test]
    public async Task SetFilterOverrides_DryRun_DoesNotTouchTheModel()
    {
        var filterId = CreateFilterElement("SC_Filter_Overrides_Dry");

        var tool = CreateOverridesTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"filterId\":" + filterId +
            ",\"color\":\"red\",\"dryRun\":true}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var view = (View)Doc.GetElement(ElementIdCompat.Create(_viewId));
        await Assert.That(view.GetFilters().Count).IsEqualTo(0);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetFilterOverrides_UnknownFilter_ListsKnownNames()
    {
        CreateFilterElement("SC_Filter_Known");

        var tool = CreateOverridesTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"filterName\":\"SC_Filter_Missing\"," +
            "\"color\":\"red\",\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("filter_not_found");
        var known = result.Data!.Value.GetProperty("knownFilters");
        await Assert.That(known.GetArrayLength()).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task SetFilterOverrides_WithoutSettings_ReturnsInvalidArguments()
    {
        var filterId = CreateFilterElement("SC_Filter_NoSettings");

        var tool = CreateOverridesTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"filterId\":" + filterId + ",\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
    }

    [Test]
    public async Task Undo_RemovesCreatedFilterAndRestoresViewState()
    {
        if (!_wallsCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateTool();
        var create = tool.ExecuteForTests(
            Parse("{\"viewIds\":[" + _viewId + "],\"categories\":[\"OST_Walls\"]," +
                  "\"conditions\":[{\"parameter\":\"ALL_MODEL_INSTANCE_COMMENTS\",\"op\":\"eq\"," +
                  "\"value\":\"" + WallComment + "\"}],\"visibility\":\"hide\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(create.Success).IsTrue();
        await Assert.That(CountFilters()).IsEqualTo(1);

        var undo = new UndoLastTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undoResult = undo.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undoResult.Success).IsTrue();
        await Assert.That(CountFilters()).IsEqualTo(0);

        var view = (View)Doc.GetElement(ElementIdCompat.Create(_viewId));
        await Assert.That(view.GetFilters().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Undo_RestoresOverrides()
    {
        var filterId = CreateFilterElement("SC_Filter_Undo");

        var tool = CreateOverridesTool();
        var apply = tool.ExecuteForTests(
            Parse("{\"viewIds\":[" + _viewId + "],\"filterId\":" + filterId +
                  ",\"color\":\"#00FF00\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(apply.Success).IsTrue();

        var undo = new UndoLastTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undoResult = undo.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undoResult.Success).IsTrue();
        var view = (View)Doc.GetElement(ElementIdCompat.Create(_viewId));
        var applied = false;
        foreach (var id in view.GetFilters())
        {
            if (id.GetValue() == filterId)
                applied = true;
        }

        await Assert.That(applied).IsFalse();
    }

    private CreateViewFilterTool CreateTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private SetFilterOverridesTool CreateOverridesTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private int CountFilters()
    {
        var count = 0;
        foreach (var element in new FilteredElementCollector(Doc)
                     .OfClass(typeof(ParameterFilterElement)))
        {
            _ = element;
            count++;
        }

        return count;
    }

    private long CreateFilterElement(string name)
    {
        long id = 0;
        _transactions!.RunInTransaction(Doc, "Seed filter element", doc =>
        {
            var filter = ParameterFilterElement.Create(
                doc,
                name,
                new List<ElementId> { new(BuiltInCategory.OST_Walls) });
            id = filter.Id.GetValue();
        });
        return id;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}