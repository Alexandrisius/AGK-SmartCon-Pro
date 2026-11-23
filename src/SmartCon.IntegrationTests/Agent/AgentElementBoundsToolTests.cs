using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// get_element_bounds (night run 2, N-ds-8): a curve-driven element reports
/// its location curve and bounding box in millimetres, a family instance
/// reports its insertion point, a type is not applicable, elements without
/// placement are named, and unknown ids land in missing[].
/// </summary>
public sealed class AgentElementBoundsToolTests : RevitApiTest
{
    private const string FamilyName = "SC_AgentElementBounds_Family";

    private Document? _document;
    private FamilySeed? _seed;
    private RevitThreadEvent? _event;
    private long _wallId;
    private long _levelId;
    private long _instanceId;
    private bool _hasWall;

    private Document Doc => _document!;

    private RevitThreadEvent Event => _event!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _event = new RevitThreadEvent(Application);
        RunInTransaction("Seed bounds model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            _levelId = level.Id.GetValue();
            var wall = ModelSeed.TryCreateWall(doc, level.Id);
            _hasWall = wall is not null;
            _wallId = wall?.Id.GetValue() ?? 0;
        });
    }

    [After(Test)]
    public void Close()
    {
        _seed?.Dispose();
        try
        {
            _document?.Close(false);
        }
        catch (Exception)
        {
            // The document may already be closed by a test scenario.
        }
    }

    [Test]
    public async Task Wall_CurveLocationAndBoundingBoxInMillimetres()
    {
        RequireWall();

        var result = await Run(new { elementIds = new[] { _wallId } });

        await Assert.That(result.Success).IsTrue();
        var element = FirstElement(result);
        await Assert.That(element.GetProperty("applicable").GetBoolean()).IsTrue();

        var location = element.GetProperty("location");
        await Assert.That(location.GetProperty("kind").GetString()).IsEqualTo("curve");
        await Assert.That(location.GetProperty("lengthMm").GetDouble()).IsGreaterThan(0);
        await Assert.That(location.TryGetProperty("start", out _)).IsTrue();
        await Assert.That(location.TryGetProperty("end", out _)).IsTrue();

        var box = element.GetProperty("boundingBox");
        await Assert.That(box.GetProperty("size").GetProperty("x").GetDouble())
            .IsGreaterThan(0);
        await Assert.That(result.Data!.Value.GetProperty("units").GetString()).IsEqualTo("mm");
    }

    [Test]
    public async Task FamilyInstance_PointLocation()
    {
        var placed = EnsurePlacedInstance();

        var result = await Run(new { elementIds = new[] { placed } });

        await Assert.That(result.Success).IsTrue();
        var element = FirstElement(result);
        await Assert.That(element.GetProperty("applicable").GetBoolean()).IsTrue();
        var location = element.GetProperty("location");
        await Assert.That(location.GetProperty("kind").GetString()).IsEqualTo("point");
        await Assert.That(location.TryGetProperty("point", out _)).IsTrue();
    }

    [Test]
    public async Task Type_IsNotApplicableAndPointsToInstances()
    {
        RequireWall();
        var wall = Doc.GetElement(ElementIdCompat.Create(_wallId));
        var typeId = wall.GetTypeId().GetValue();

        var result = await Run(new { elementIds = new[] { typeId } });

        await Assert.That(result.Success).IsTrue();
        var element = FirstElement(result);
        await Assert.That(element.GetProperty("applicable").GetBoolean()).IsFalse();
        await Assert.That(element.GetProperty("reason").GetString()).IsEqualTo("element_type");
        await Assert.That(element.GetProperty("isElementType").GetBoolean()).IsTrue();
        await Assert.That(element.TryGetProperty("location", out var location)
                        && location.ValueKind == JsonValueKind.Null).IsTrue();
        await Assert.That(result.Message ?? string.Empty).Contains("query_elements");
    }

    [Test]
    public async Task View_HasNoPlacement()
    {
        var viewId = new FilteredElementCollector(Doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .First(view => view.ViewType == ViewType.FloorPlan)
            .Id.GetValue();

        var result = await Run(new { elementIds = new[] { viewId } });

        await Assert.That(result.Success).IsTrue();
        var element = FirstElement(result);
        await Assert.That(element.GetProperty("applicable").GetBoolean()).IsFalse();
        await Assert.That(element.GetProperty("reason").GetString()).IsEqualTo("no_placement");
    }

    [Test]
    public async Task UnknownId_LandsInMissingAlongsideValidElements()
    {
        RequireWall();

        var result = await Run(new { elementIds = new[] { _wallId, -1234567 } });

        await Assert.That(result.Success).IsTrue();
        var payload = result.Data!.Value;
        var missing = payload.GetProperty("missing").EnumerateArray()
            .Select(id => id.GetInt64())
            .ToList();
        await Assert.That(missing.Contains(-1234567)).IsTrue();
        await Assert.That(payload.GetProperty("elements").GetArrayLength()).IsEqualTo(1);
    }

    // ── Support ────────────────────────────────────────────────────────

    private void RequireWall()
    {
        if (!_hasWall)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");
    }

    private long EnsurePlacedInstance()
    {
        if (_instanceId != 0)
            return _instanceId;

        _seed = FamilySeed.Create(Application, Doc, FamilyName, out var reason);
        if (_seed is null)
            Skip.Test(reason ?? "Не удалось сидировать семейство");

        RunInTransaction("Place family instance", doc =>
        {
            var family = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitFamily))
                .Cast<RevitFamily>()
                .First(item => item.Name == FamilyName);
            var symbol = (FamilySymbol)doc.GetElement(family.GetFamilySymbolIds().First());
            if (!symbol.IsActive)
                symbol.Activate();

            var instance = doc.Create.NewFamilyInstance(
                new XYZ(5, 5, 0), symbol, StructuralType.NonStructural);
            _instanceId = instance.Id.GetValue();
        });

        return _instanceId;
    }

    private async Task<ToolResult> Run(object args)
    {
        var tool = new GetElementBoundsTool(new StubRevitContext(Doc));
        var context = new AgentToolContext(
            Event,
            AgentDocumentIdentity.GetDocumentId(Doc),
            Guid.Empty);
        return await tool.ExecuteAsync(context, Parse(args), CancellationToken.None);
    }

    private static JsonElement FirstElement(ToolResult result) =>
        result.Data!.Value.GetProperty("elements").EnumerateArray().First();

    private static JsonElement Parse(object args)
    {
        var json = JsonSerializer.Serialize(args);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private void RunInTransaction(string name, Action<Document> body)
    {
        new RevitTransactionService(new StubRevitContext(Doc)).RunInTransaction(Doc, name, body);
    }
}
