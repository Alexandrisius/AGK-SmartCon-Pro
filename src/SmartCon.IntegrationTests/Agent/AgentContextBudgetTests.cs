using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Context-budget behaviour on a model larger than the per-call limits
/// (mvp-plan §11): pagination, truncation flags and the broad-query guard.
/// </summary>
public sealed class AgentContextBudgetTests : RevitApiTest
{
    private const int SeededLevels = 1100;

    private Document? _document;
    private RevitTransactionService? _transactions;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));

        _transactions.RunInTransaction(Doc, "Seed levels for budget test", doc =>
        {
            for (var index = 0; index < SeededLevels; index++)
                Level.Create(doc, 10.0 + index * 0.5);
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task QueryElements_LargeModel_IsPaginatedAndBounded()
    {
        var tool = new QueryElementsTool(new StubRevitContext(Doc));
        var args = Parse(
            """{"categories":["OST_Levels"],"limit":50,"fields":["elementId","name","category"]}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("total").GetInt32()).IsGreaterThanOrEqualTo(SeededLevels);
        await Assert.That(data.GetProperty("returned").GetInt32()).IsEqualTo(50);
        await Assert.That(data.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("nextOffset").GetInt32()).IsEqualTo(50);

        var elements = data.GetProperty("elements");
        await Assert.That(elements.GetArrayLength()).IsEqualTo(50);
        await Assert.That(elements[0].TryGetProperty("elementId", out _)).IsTrue();
        await Assert.That(elements[0].TryGetProperty("name", out _)).IsTrue();
        await Assert.That(elements[0].TryGetProperty("family", out _)).IsFalse();
    }

    [Test]
    public async Task QueryElements_ResultStaysWithinCharacterBudget()
    {
        var tool = new QueryElementsTool(new StubRevitContext(Doc));
        var args = Parse("""{"categories":["OST_Levels"],"limit":200}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var json = ToolResultBudget.Serialize(result);
        await Assert.That(json.Length).IsLessThan(ToolResultBudget.DefaultMaxCharacters);

        using var document = JsonDocument.Parse(json);
        await Assert.That(document.RootElement.GetProperty("success").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task QueryElements_WithoutFilters_ReturnsQueryTooBroad()
    {
        var tool = new QueryElementsTool(new StubRevitContext(Doc));
        var args = Parse("""{"limit":200}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("query_too_broad");
    }

    [Test]
    public async Task ListViews_SummaryOnly_ReturnsCounts()
    {
        var tool = new ListViewsTool(new StubRevitContext(Doc));
        var args = Parse("""{"summaryOnly":true}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("summaryOnly").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("byType").GetArrayLength()).IsGreaterThan(0);
        await Assert.That(data.TryGetProperty("views", out _)).IsFalse();
    }

    [Test]
    public async Task ListViews_FieldAliases_ProjectTypeAndLevel()
    {
        var tool = new ListViewsTool(new StubRevitContext(Doc));
        var args = Parse("""{"types":["FloorPlan"],"limit":5,"fields":["viewId","name","type","level"]}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.TryGetProperty("ignoredFields", out _)).IsFalse();

        var view = data.GetProperty("views")[0];
        await Assert.That(view.TryGetProperty("viewId", out _)).IsTrue();
        await Assert.That(view.TryGetProperty("name", out _)).IsTrue();
        await Assert.That(view.TryGetProperty("viewType", out var type)).IsTrue();
        await Assert.That(type.GetString()).IsEqualTo("FloorPlan");
        await Assert.That(view.TryGetProperty("levelName", out _)).IsTrue();
    }

    [Test]
    public async Task ListViews_NullValuedFields_AreStillProjected()
    {
        var tool = new ListViewsTool(new StubRevitContext(Doc));
        var args = Parse("""{"types":["FloorPlan"],"limit":5,"fields":["viewId","template","sheet"]}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.TryGetProperty("ignoredFields", out _)).IsFalse();

        var view = data.GetProperty("views")[0];
        await Assert.That(view.TryGetProperty("templateName", out _)).IsTrue();
        await Assert.That(view.TryGetProperty("sheetNumber", out _)).IsTrue();
    }

    [Test]
    public async Task ListViews_UnknownField_IsReported()
    {
        var tool = new ListViewsTool(new StubRevitContext(Doc));
        var args = Parse("""{"types":["FloorPlan"],"limit":5,"fields":["nonsense"]}""");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("ignoredFields")[0].GetString()).IsEqualTo("nonsense");
        await Assert.That(data.GetProperty("availableFields").GetArrayLength()).IsGreaterThan(0);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
