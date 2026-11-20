using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
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
        await Assert.That(result.Data.Value.GetProperty("created").GetBoolean()).IsFalse();
        await Assert.That(result.Data.Value.GetProperty("wouldCreate").GetBoolean()).IsTrue();
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
        await Assert.That(data.GetProperty("changed").GetArrayLength())
            .IsGreaterThanOrEqualTo(1);

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

    [Test]
    public async Task ListFilters_WithScheduleInTheModel_DoesNotFail()
    {
        // External run 6, P0: View.GetFilters() throws
        // "The view type does not support Visibility/Graphics Overriddes" for a
        // schedule, and one such view failed the whole usage map.
        _transactions!.RunInTransaction(Doc, "Seed schedule", doc =>
            ModelSeed.CreateScheduleChecked(doc));

        var tool = new ListFiltersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("appliedViewCountComputed")
            .GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ListFilters_ViewId_NarrowsTheListToOneFilter()
    {
        var appliedId = CreateFilterElement("SC_Filter_Applied");
        CreateFilterElement("SC_Filter_NotApplied");
        _transactions!.RunInTransaction(Doc, "Apply filter to view", doc =>
        {
            var view = (View)doc.GetElement(ElementIdCompat.Create(_viewId));
            view.AddFilter(ElementIdCompat.Create(appliedId));
        });

        var tool = new ListFiltersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc, Parse("{\"viewId\":" + _viewId + "}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("narrowedByView").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("filters")[0].GetProperty("filterId").GetInt64())
            .IsEqualTo(appliedId);
        await Assert.That(data.GetProperty("filters")[0].GetProperty("appliedViewCount")
            .GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task ListFilters_WithoutAppliedViews_ReportsNotComputed()
    {
        CreateFilterElement("SC_Filter_NotComputed");

        var tool = new ListFiltersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc, Parse("{\"includeAppliedViews\":false}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("appliedViewCountComputed").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("filters")[0].GetProperty("appliedViewCount")
            .GetString()).IsEqualTo("notComputed");
    }

    [Test]
    public async Task GetFilterDetails_WithScheduleInTheModel_ReturnsTheFilter()
    {
        var filterId = CreateFilterElement("SC_Filter_Details");
        _transactions!.RunInTransaction(Doc, "Seed schedule", doc =>
            ModelSeed.CreateScheduleChecked(doc));

        var tool = new GetFilterDetailsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc, Parse("{\"filterId\":" + filterId + "}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("name").GetString()).IsEqualTo("SC_Filter_Details");
        await Assert.That(data.GetProperty("appliedViewCount").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("appliedViewCountComputed").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task GetFilterDetails_AppliedViews_ArePaged()
    {
        var filterId = CreateFilterElement("SC_Filter_Paged");
        _transactions!.RunInTransaction(Doc, "Seed second view and apply filter", doc =>
        {
            var second = ModelSeed.CreateViewPlanChecked(
                doc,
                ElementIdCompat.Create(_levelId),
                "SC_FilterPlan2");
            foreach (var viewId in new[] { _viewId, second.Id.GetValue() })
            {
                var view = (View)doc.GetElement(ElementIdCompat.Create(viewId));
                view.AddFilter(ElementIdCompat.Create(filterId));
            }
        });

        var tool = new GetFilterDetailsTool(new StubRevitContext(Doc));
        var first = tool.ExecuteForTests(
            Doc,
            Parse(
                "{\"filterId\":" + filterId +
                ",\"appliedViewsLimit\":1,\"appliedViewsOffset\":0}"),
            CancellationToken.None);

        await Assert.That(first.Success).IsTrue();
        var data = first.Data!.Value;
        await Assert.That(data.GetProperty("appliedViewsTotal").GetInt32()).IsEqualTo(2);
        await Assert.That(data.GetProperty("appliedViewsReturned").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("appliedViewsOffset").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("appliedViewsNextOffset").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("appliedViewsTruncated").GetBoolean()).IsTrue();

        var second = tool.ExecuteForTests(
            Doc,
            Parse(
                "{\"filterId\":" + filterId +
                ",\"appliedViewsLimit\":1,\"appliedViewsOffset\":1}"),
            CancellationToken.None);

        await Assert.That(second.Success).IsTrue();
        var secondData = second.Data!.Value;
        await Assert.That(secondData.GetProperty("appliedViewsReturned").GetInt32())
            .IsEqualTo(1);
        await Assert.That(secondData.GetProperty("appliedViewsTruncated").GetBoolean())
            .IsFalse();
    }

    [Test]
    public async Task GetFilterDetails_WithoutArguments_AsksForIdOrName()
    {
        var tool = new GetFilterDetailsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("filter_not_found");
        await Assert.That(result.Message!.Contains("Pass filterId or filterName")).IsTrue();
    }

    [Test]
    public async Task SetFilterRules_DryRun_ReturnsThePreviousRuleTree()
    {
        var filterId = CreateFilterWithRule("SC_Filter_WithRule");

        var tool = new SetFilterRulesTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"filterId\":" + filterId + ",\"conditions\":[],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var previous = data.GetProperty("previousRules");
        await Assert.That(previous.GetProperty("type").GetString()).IsEqualTo("parameter");
        var rules = previous.GetProperty("rules");
        await Assert.That(rules.GetArrayLength()).IsEqualTo(1);
        await Assert.That(rules[0].GetProperty("parameterId").GetInt64()).IsLessThan(0);
        await Assert.That(
            !string.IsNullOrEmpty(rules[0].GetProperty("parameterName").GetString())).IsTrue();
        await Assert.That(rules[0].GetProperty("op").GetString()!.Contains("Equals")).IsTrue();
        await Assert.That(rules[0].GetProperty("value").GetString()).IsEqualTo("x");
        await Assert.That(data.GetProperty("previousCategories").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("previousCategoryIds").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
public async Task ListFilters_ReportsRulesDigestAndDuplicateGroups()
{
    CreateFilterWithRule("SC_Dup_A");
    CreateFilterWithRule("SC_Dup_B");

    var tool = new ListFiltersTool(new StubRevitContext(Doc));
    var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    var data = result.Data!.Value;
    string? digestA = null;
    string? digestB = null;
    foreach (var filter in data.GetProperty("filters").EnumerateArray())
    {
        var name = filter.GetProperty("name").GetString();
        if (name == "SC_Dup_A")
            digestA = filter.GetProperty("rulesDigest").GetString();
        else if (name == "SC_Dup_B")
            digestB = filter.GetProperty("rulesDigest").GetString();
    }

    await Assert.That(string.IsNullOrEmpty(digestA)).IsFalse();
    await Assert.That(digestA).IsEqualTo(digestB);

    var groups = data.GetProperty("duplicateGroups");
    await Assert.That(groups.GetArrayLength()).IsGreaterThanOrEqualTo(1);
    await Assert.That(data.GetProperty("duplicateGroupCount").GetInt32())
        .IsGreaterThanOrEqualTo(1);
}

[Test]
public async Task GetFilterDetails_IncludeOverrides_ReportsPerViewValues()
{
    var filterId = CreateFilterElement("SC_Filter_OverridesRead");
    _transactions!.RunInTransaction(Doc, "Apply filter with overrides", doc =>
    {
        var view = (View)doc.GetElement(ElementIdCompat.Create(_viewId));
        var elementId = ElementIdCompat.Create(filterId);
        view.AddFilter(elementId);
        var settings = new OverrideGraphicSettings();
        settings.SetProjectionLineColor(new Color(255, 0, 0));
        settings.SetSurfaceTransparency(25);
        view.SetFilterOverrides(elementId, settings);
    });

    var tool = new GetFilterDetailsTool(new StubRevitContext(Doc));
    var result = tool.ExecuteForTests(
        Doc,
        Parse("{\"filterId\":" + filterId + ",\"includeOverrides\":true}"),
        CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    var applied = result.Data!.Value.GetProperty("appliedViews");
    await Assert.That(applied.GetArrayLength()).IsEqualTo(1);
    await Assert.That(applied[0].GetProperty("visible").GetBoolean()).IsTrue();
    var overrides = applied[0].GetProperty("overrides");
    await Assert.That(overrides.GetProperty("projectionLineColor").GetString())
        .IsEqualTo("#FF0000");
    await Assert.That(overrides.GetProperty("transparency").GetInt32()).IsEqualTo(25);
}

[Test]
public async Task RenameFilter_Apply_AndUndoRestoresTheName()
{
    var filterId = CreateFilterElement("SC_Filter_Rename");

    var tool = new RenameFilterTool(
        new StubRevitContext(Doc), _transactions!, _changeSets!);
    var result = tool.ExecuteForTests(
        Parse("{\"filterId\":" + filterId + ",\"newName\":\"SC_Filter_Renamed\"," +
              "\"dryRun\":false}"),
        approved: true,
        CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    var renamed = (ParameterFilterElement)Doc.GetElement(ElementIdCompat.Create(filterId));
    await Assert.That(renamed.Name).IsEqualTo("SC_Filter_Renamed");
    await Assert.That(_changeSets!.All.Count).IsEqualTo(1);

    var undo = new UndoLastTool(
        new StubRevitContext(Doc), _transactions!, _changeSets!);
    var undoResult = undo.ExecuteForTests(
        Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

    await Assert.That(undoResult.Success).IsTrue();
    await Assert.That(renamed.Name).IsEqualTo("SC_Filter_Rename");
}

[Test]
public async Task RenameFilter_DuplicateName_IsRefused()
{
    CreateFilterElement("SC_Filter_Taken");
    var filterId = CreateFilterElement("SC_Filter_Free");

    var tool = new RenameFilterTool(
        new StubRevitContext(Doc), _transactions!, _changeSets!);
    var result = tool.ExecuteForTests(
        Parse("{\"filterId\":" + filterId + ",\"newName\":\"SC_Filter_Taken\"," +
              "\"dryRun\":false}"),
        approved: true,
        CancellationToken.None);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.ErrorCode).IsEqualTo("duplicate_name");
    var filter = (ParameterFilterElement)Doc.GetElement(ElementIdCompat.Create(filterId));
    await Assert.That(filter.Name).IsEqualTo("SC_Filter_Free");
}

[Test]
    public async Task CreateViewFilter_VisibilityNone_DisablesTheFilter()
    {
        if (!_wallsCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"categories\":[\"OST_Walls\"]," +
            "\"visibility\":\"none\",\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var filterId = ElementIdCompat.Create(
            result.Data!.Value.GetProperty("filterId").GetInt64());
        var view = (View)Doc.GetElement(ElementIdCompat.Create(_viewId));

        // The filter stays on the view but is disabled: elements are visible again.
        await Assert.That(view.GetFilters().Count).IsEqualTo(1);
        await Assert.That(view.GetIsFilterEnabled(filterId)).IsFalse();
        var readBack = result.Data!.Value.GetProperty("readBack");
        await Assert.That(readBack[0].GetProperty("filterEnabled").GetBoolean()).IsFalse();
        await Assert.That(readBack[0].GetProperty("mode").GetString()).IsEqualTo("none");
    }

    [Test]
    public async Task CreateViewFilter_VisibilityShow_KeepsTheFilterEnabled()
    {
        if (!_wallsCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateTool();
        var args = Parse(
            "{\"viewIds\":[" + _viewId + "],\"categories\":[\"OST_Walls\"]," +
            "\"visibility\":\"show\",\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var filterId = ElementIdCompat.Create(
            result.Data!.Value.GetProperty("filterId").GetInt64());
        var view = (View)Doc.GetElement(ElementIdCompat.Create(_viewId));
        await Assert.That(view.GetIsFilterEnabled(filterId)).IsTrue();
        await Assert.That(view.GetFilterVisibility(filterId)).IsTrue();
        var readBack = result.Data!.Value.GetProperty("readBack");
        await Assert.That(readBack[0].GetProperty("mode").GetString()).IsEqualTo("show");
    }

    [Test]
    public async Task SetFilterRules_DryRun_AppliesAndRollsBack()
    {
        var filterId = CreateFilterWithRule("SC_Filter_DryRollback");

        var tool = new SetFilterRulesTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"filterId\":" + filterId + ",\"conditions\":[{\"parameter\":" +
                  "\"ALL_MODEL_INSTANCE_COMMENTS\",\"op\":\"eq\",\"value\":\"y\"}]," +
                  "\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("validated").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("wouldUpdate").GetBoolean()).IsTrue();
        await Assert.That(
                result.Warnings.Any(warning => warning.Contains("undo_last"))).IsTrue();

        // The dry run applied the new rule inside the transaction: the model must
        // still hold the previous rule value after the rollback.
        var details = new GetFilterDetailsTool(new StubRevitContext(Doc));
        var readBack = details.ExecuteForTests(
            Doc, Parse("{\"filterId\":" + filterId + "}"), CancellationToken.None);
        await Assert.That(readBack.Success).IsTrue();
        var rules = readBack.Data!.Value.GetProperty("rules").GetProperty("rules");
        await Assert.That(rules.GetArrayLength()).IsEqualTo(1);
        await Assert.That(rules[0].GetProperty("value").GetString()).IsEqualTo("x");
    }

    [Test]
    public async Task SetFilterRules_IdenticalRules_ReturnsNoChanges()
    {
        var filterId = CreateFilterElement("SC_Filter_NoRules");

        var tool = new SetFilterRulesTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"filterId\":" + filterId + ",\"conditions\":[],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("errorCode").GetString()).IsEqualTo("no_changes");
        await Assert.That(data.GetProperty("wouldUpdate").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task ListFilterableParameters_ForWalls_ReturnsCommonParameters()
    {
        var walls = Category.GetCategory(Doc, BuiltInCategory.OST_Walls);
        if (walls is null)
            Skip.Test("Категория стен недоступна в документе");

        var tool = new ListFilterableParametersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"categoryIds\":[" + walls!.Id.GetValue() + "]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("parameters").GetArrayLength())
            .IsGreaterThan(0);
        var first = data.GetProperty("parameters")[0];
        await Assert.That(first.GetProperty("supportedByAllCategories").GetBoolean())
            .IsTrue();
        await Assert.That(first.GetProperty("missingFromCategories").GetArrayLength())
            .IsEqualTo(0);
    }

    [Test]
    public async Task ListFilterableParameters_UnsupportedParameter_ReportsMissingCategories()
    {
        var walls = Category.GetCategory(Doc, BuiltInCategory.OST_Walls);
        var grids = Category.GetCategory(Doc, BuiltInCategory.OST_Grids);
        if (walls is null || grids is null)
            Skip.Test("Категории стен или сеток недоступны в документе");

        var tool = new ListFilterableParametersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"categoryIds\":[" + walls!.Id.GetValue() + "," +
                  grids!.Id.GetValue() + "],\"parameterName\":\"RBS_SYSTEM_NAME_PARAM\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var parameters = result.Data!.Value.GetProperty("parameters");
        await Assert.That(parameters.GetArrayLength()).IsEqualTo(1);
        await Assert.That(parameters[0].GetProperty("supportedByAllCategories")
            .GetBoolean()).IsFalse();
        await Assert.That(parameters[0].GetProperty("missingFromCategories")
            .GetArrayLength()).IsGreaterThanOrEqualTo(1);
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

    private long CreateFilterWithRule(string name)
    {
        long id = 0;
        _transactions!.RunInTransaction(Doc, "Seed filter with rule", doc =>
        {
            var rule = ParameterFilterRuleFactory.CreateEqualsRule(
                new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS),
                "x");
            var elementFilter = new ElementParameterFilter(rule);
            var filter = ParameterFilterElement.Create(
                doc,
                name,
                new List<ElementId> { new(BuiltInCategory.OST_Walls) },
                elementFilter);
            id = filter.Id.GetValue();
        });
        return id;
    }
}