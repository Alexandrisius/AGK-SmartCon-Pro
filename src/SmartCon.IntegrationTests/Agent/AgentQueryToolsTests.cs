using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

public sealed class AgentQueryToolsTests : RevitApiTest
{
    private const string SeededLevelName = "SC_AgentQuery_Level";
    private const string SeededViewName = "SC_AgentQuery_Plan";
    private const string SeededComment = "AgentSeed";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private bool _wallCreated;
    private long _wallId;
    private long _viewId;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _transactions.RunInTransaction(Doc, "Seed agent query model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = SeededLevelName;
            var view = ModelSeed.CreateViewPlanChecked(doc, level.Id, SeededViewName);
            _viewId = view?.Id.GetValue() ?? 0;

            var wall = ModelSeed.TryCreateWall(doc, level.Id);
            _wallCreated = wall is not null;
            _wallId = wall?.Id.GetValue() ?? 0;
            wall?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(SeededComment);
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task QueryElements_FiltersByCategoryAndParameterCondition()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = new QueryElementsTool(new StubRevitContext(Doc));
        var args = Parse(
            """{"categories":["OST_Walls"],"conditions":[{"parameter":"Comments","op":"contains","value":"AgentSeed"}],"limit":10}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("total").GetInt32()).IsGreaterThanOrEqualTo(1);
        var elements = data.GetProperty("elements");
        await Assert.That(elements.GetArrayLength()).IsGreaterThanOrEqualTo(1);
        await Assert.That(elements[0].GetProperty("category").GetString()).IsEqualTo("OST_Walls");
    }

    [Test]
    public async Task QueryElements_NumericConditionOnTextParameter_DoesNotSilentlyReturnZero()
    {
        // Benchmark run 2, task 4: a numeric condition on a text parameter returned 0
        // elements while the schedule showed values. Scalar values are coerced now.
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        _transactions!.RunInTransaction(Doc, "Set numeric-looking comment", doc =>
        {
            var wall = (Wall?)doc.GetElement(ElementIdCompat.Create(_wallId));
            wall?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("0");
        });

        var tool = new QueryElementsTool(new StubRevitContext(Doc));
        var args = Parse(
            """{"categories":["OST_Walls"],"conditions":[{"parameter":"ALL_MODEL_INSTANCE_COMMENTS","op":"gte","value":0}],"limit":10}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("total").GetInt32())
            .IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task QueryElements_UnknownCategory_ReturnsStructuredFailure()
    {
        var tool = new QueryElementsTool(new StubRevitContext(Doc));
        var args = Parse("""{"categories":["OST_NoSuchCategory"]}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unknown_category");
    }

    [Test]
    public async Task GetElementDetails_ReturnsInstanceAndTypeParameters()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = new GetElementDetailsTool(new StubRevitContext(Doc));
        var args = Parse("""{"elementIds":[""" + _wallId + """],"includeTypeParameters":true}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var element = result.Data!.Value.GetProperty("elements")[0];
        await Assert.That(element.GetProperty("elementId").GetInt64()).IsEqualTo(_wallId);
        var typeId = element.GetProperty("typeId").GetInt64();
        await Assert.That(typeId).IsGreaterThan(0);
        await Assert.That(element.GetProperty("instanceParameters").GetArrayLength())
            .IsGreaterThan(0);

        var types = result.Data!.Value.GetProperty("types");
        await Assert.That(types.GetArrayLength()).IsEqualTo(1);
        var type = types[0];
        await Assert.That(type.GetProperty("typeId").GetInt64()).IsEqualTo(typeId);
        var typeParameters = type.GetProperty("parameters");
        await Assert.That(typeParameters.GetArrayLength()).IsGreaterThan(0);
        foreach (var parameter in typeParameters.EnumerateArray())
        {
            await Assert.That(parameter.GetProperty("scope").GetString()).IsEqualTo("type");
        }
    }

    [Test]
    public async Task GetElementDetails_LimitsAndFlagsTruncation()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = new GetElementDetailsTool(new StubRevitContext(Doc));
        var args = Parse(
            "{\"elementIds\":[" + _wallId +
            "],\"includeTypeParameters\":true,\"maxParametersPerElement\":1}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var element = result.Data!.Value.GetProperty("elements")[0];
        await Assert.That(element.GetProperty("instanceParameters").GetArrayLength())
            .IsLessThanOrEqualTo(1);
        await Assert.That(element.GetProperty("instanceParametersTruncated").GetBoolean()).IsTrue();

        var type = result.Data!.Value.GetProperty("types")[0];
        await Assert.That(type.GetProperty("parameters").GetArrayLength()).IsLessThanOrEqualTo(1);
        await Assert.That(type.GetProperty("parametersTruncated").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task SummarizeElements_GroupsByCategory()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = new SummarizeElementsTool(new StubRevitContext(Doc));
        var args = Parse(
            """{"categories":["OST_Walls"],"groupBy":"category","sumParameter":"Length"}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var groups = result.Data!.Value.GetProperty("groups");
        await Assert.That(groups.GetArrayLength()).IsGreaterThanOrEqualTo(1);
        await Assert.That(groups[0].GetProperty("key").GetString()).IsEqualTo("OST_Walls");
        await Assert.That(groups[0].GetProperty("count").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(result.Data.Value.TryGetProperty("groupsTotal", out _)).IsTrue();
        await Assert.That(result.Data.Value.TryGetProperty("groupsSkippedInternal", out _)).IsTrue();
    }

    [Test]
    public async Task ListViewTemplates_ReturnsTemplateCreatedFromView()
    {
        var created = false;
        _transactions!.RunInTransaction(Doc, "Create agent template", doc =>
        {
            created = FindSeededView(doc)?.CreateViewTemplate() is not null;
        });
        await Assert.That(created).IsTrue();

        var tool = new ListViewTemplatesTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(Doc, EmptyArgs(), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("total").GetInt32()).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task GetViewSettings_ReturnsSeededPlanSettings()
    {
        if (_viewId == 0)
            Skip.Test("План этажа не создан");

        var tool = new GetViewSettingsTool(new StubRevitContext(Doc));
        var args = Parse("""{"viewId":""" + _viewId + ""","compact":false}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("name").GetString()).IsEqualTo(SeededViewName);
        await Assert.That(data.GetProperty("viewType").GetString()).IsEqualTo("FloorPlan");
        await Assert.That(data.GetProperty("viewRange").ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(data.GetProperty("crop").ValueKind).IsEqualTo(JsonValueKind.Object);
    }

    [Test]
    public async Task GetViewTemplateDetails_ReturnsTemplateMetadata()
    {
        long templateId = 0;
        _transactions!.RunInTransaction(Doc, "Create agent template", doc =>
        {
            templateId = FindSeededView(doc)?.CreateViewTemplate()?.Id.GetValue() ?? 0;
        });
        if (templateId == 0)
            Skip.Test("Шаблон вида не создан");

        var tool = new GetViewTemplateDetailsTool(new StubRevitContext(Doc));
        var args = Parse("""{"templateId":""" + templateId + "}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("template").GetProperty("templateId").GetInt64())
            .IsEqualTo(templateId);
        await Assert.That(data.GetProperty("settings").GetProperty("name").GetString())
            .IsNotEmpty();
    }

    private View? FindSeededView(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(view => view.Name == SeededViewName);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement EmptyArgs()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}