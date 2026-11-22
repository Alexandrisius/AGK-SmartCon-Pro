using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nice3point.TUnit.Revit;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Element-query honesty: a condition may name a structural field of the element
/// (name, family, type, category, level), and a parameter that exists on no
/// scanned element is reported as unknown_parameter instead of an empty result
/// (partials: <c>.UnknownParameter</c>, <c>.Summarize</c>).
/// </summary>
public sealed partial class AgentElementQueryHonestyTests : RevitApiTest
{
    private const string LevelName = "SC_AgentQueryHonesty_Level";
    private const string FamilyName = "SC_AgentQueryHonesty_Family";

    private Document? _document;
    private FamilySeed? _seed;
    private bool _hasWall;
    private long _wallId;
    private long _levelId;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        RunInTransaction("Seed element query model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = LevelName;
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
        Doc.Close(false);
    }

    [Test]
    public async Task QueryElements_NameContainsPartOfTypeName_FindsElement()
    {
        RequireWall();
        var typeName = WallTypeOf().Name;

        var result = Query(WallsQuery(Condition("name", "contains", typeName.ToUpperInvariant())));

        await Assert.That(result.Success).IsTrue();
        await Assert.That(ElementIds(result).Contains(_wallId)).IsTrue();

        var miss = Query(WallsQuery(Condition("name", "contains", "zz-no-such-name")));
        await Assert.That(miss.Success).IsTrue();
        await Assert.That(Total(miss)).IsEqualTo(0);
    }

    [Test]
    public async Task QueryElements_NameOfFamilyInstance_MatchesFamilyOrTypeName()
    {
        var instanceId = PlaceFamilyInstance();
        var instance = (FamilyInstance)Doc.GetElement(ElementIdCompat.Create(instanceId));
        var category = instance.Category?.Name ?? string.Empty;

        var fragments = new[]
        {
            FamilyName.Substring(4).ToLowerInvariant(),
            FamilySeed.Type1.ToLowerInvariant()
        };
        foreach (var fragment in fragments)
        {
            var result = Query(CategoriesQuery(category, Condition("name", "contains", fragment)));
            await Assert.That(result.Success).IsTrue();
            await Assert.That(ElementIds(result).Contains(instanceId)).IsTrue();
        }

        // The instance is named by its type: neq on the type name excludes it.
        var byType = Query(CategoriesQuery(category, Condition("name", "neq", instance.Symbol?.Name ?? "?")));
        await Assert.That(byType.Success).IsTrue();
        await Assert.That(ElementIds(byType).Contains(instanceId)).IsFalse();

        var other = Query(CategoriesQuery(category, Condition("name", "neq", "zz-no-such-name")));
        await Assert.That(ElementIds(other).Contains(instanceId)).IsTrue();

        // Nothing of the three names carries the fragment: an honest empty result.
        var miss = Query(CategoriesQuery(category, Condition("name", "contains", "zz-no-such-name")));
        await Assert.That(miss.Success).IsTrue();
        await Assert.That(Total(miss)).IsEqualTo(0);

        // The instance is not level-based: the level is absent, not unknown.
        var noLevel = Query(CategoriesQuery(category, Condition("level", "notExists")));
        await Assert.That(noLevel.Success).IsTrue();
        await Assert.That(ElementIds(noLevel).Contains(instanceId)).IsTrue();
        var withLevel = Query(CategoriesQuery(category, Condition("level", "exists")));
        await Assert.That(withLevel.Success).IsTrue();
        await Assert.That(ElementIds(withLevel).Contains(instanceId)).IsFalse();
    }

    [Test]
    public async Task QueryElements_StructuralFields_FilterTheElement()
    {
        RequireWall();
        var wall = WallElement;
        var type = WallTypeOf();
        var family = type.FamilyName ?? string.Empty;

        var matching = new[]
        {
            Condition("family", "eq", family),
            Condition(" FAMILY ", "eq", family.ToUpperInvariant()),
            Condition("type", "eq", type.Name ?? string.Empty),
            Condition("type", "contains", (type.Name ?? string.Empty).ToLowerInvariant()),
            Condition("category", "eq", "ost_walls"),
            Condition("category", "eq", wall.Category?.Name ?? string.Empty),
            ConditionRaw("category", "in", "[\"OST_Walls\",\"OST_Doors\"]"),
            Condition("level", "eq", LevelName),
            Condition(" LeVeL nAmE ", "eq", LevelName.ToUpperInvariant()),
            Condition("family", "exists"),
            Condition("type", "exists"),
            Condition("category", "exists"),
            Condition("level", "exists"),
            Condition("name", "exists")
        };
        var excluding = new[]
        {
            Condition("family", "neq", family),
            ConditionRaw("family", "notIn", "[" + Json(family) + "]"),
            Condition("family", "notExists"),
            Condition("level", "notExists"),
            ConditionRaw("category", "notIn", "[\"ost_walls\"]"),
            Condition("name", "neq", wall.Name ?? string.Empty)
        };

        var problems = new List<string>();
        foreach (var condition in matching)
        {
            var result = Query(WallsQuery(condition));
            if (!result.Success || !ElementIds(result).Contains(_wallId))
                problems.Add(condition + " -> " + Outcome(result));
        }

        foreach (var condition in excluding)
        {
            var result = Query(WallsQuery(condition));
            if (!result.Success || ElementIds(result).Contains(_wallId))
                problems.Add(condition + " -> " + Outcome(result));
        }

        await Assert.That(string.Join(" || ", problems)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task QueryElements_StructuralField_ShadowsProjectParameterOfSameName()
    {
        RequireWall();
        var typeName = WallTypeOf().Name ?? string.Empty;
        using var sharedFile = SharedParameterFileScope.Create(Application);
        var definitionId = Guid.Parse("8D3A5B7C-6E4F-4A2B-9C1D-0E2F4A6B8C0D");

        RunInTransaction("Bind project parameter named 'name'", doc =>
        {
            var file = Application.OpenSharedParameterFile();
            var group = file!.Groups.Create("SC_AgentQueryHonesty");
            var definition = (ExternalDefinition)group.Definitions.Create(
                new ExternalDefinitionCreationOptions("name", SpecTypeId.String.Text)
                {
                    GUID = definitionId
                });
            var categories = Application.Create.NewCategorySet();
            categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_Walls)!);
            var binding = Application.Create.NewInstanceBinding(categories);
            if (!doc.ParameterBindings.Insert(definition, binding))
                Skip.Test("Revit не привязал общий параметр 'name' к стенам");

            var wall = doc.GetElement(ElementIdCompat.Create(_wallId));
            if (wall.get_Parameter(definitionId)?.Set("SC_QH_Sentinel") != true)
                Skip.Test("Общий параметр 'name' не записался на стене");
        });

        // A project parameter named like a structural field is a reserved word:
        // its value is not what the condition "name" reads.
        var byValue = Query(WallsQuery(Condition("name", "eq", "SC_QH_Sentinel")));
        await Assert.That(byValue.Success).IsTrue();
        await Assert.That(Total(byValue)).IsEqualTo(0);

        var byTypeName = Query(WallsQuery(Condition("name", "eq", typeName)));
        await Assert.That(byTypeName.Success).IsTrue();
        await Assert.That(ElementIds(byTypeName).Contains(_wallId)).IsTrue();
    }

    [Test]
    public async Task QueryElements_FamilyNotExists_FindsElementsWithoutFamily()
    {
        RequireWall();
        long lineId = 0;
        RunInTransaction("Create model line", doc =>
        {
            // A model line has a line style rather than a family type.
            var sketchPlane = SketchPlane.Create(
                doc,
                Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
            var line = doc.Create.NewModelCurve(
                Line.CreateBound(new XYZ(0, 40, 0), new XYZ(10, 40, 0)),
                sketchPlane);
            lineId = line.Id.GetValue();
        });

        var withoutFamily = Query(CategoriesQuery("OST_Lines", Condition("family", "notExists")));
        await Assert.That(withoutFamily.Success).IsTrue();
        await Assert.That(ElementIds(withoutFamily).Contains(lineId)).IsTrue();

        var withFamily = Query(CategoriesQuery("OST_Lines", Condition("family", "exists")));
        await Assert.That(withFamily.Success).IsTrue();
        await Assert.That(ElementIds(withFamily).Contains(lineId)).IsFalse();
    }

    private void RequireWall()
    {
        if (!_hasWall)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");
    }

    private void RunInTransaction(string name, Action<Document> body)
    {
        new RevitTransactionService(new StubRevitContext(Doc)).RunInTransaction(Doc, name, body);
    }

    /// <summary>Places one instance of a loaded model family; 0 when the machine has no family template.</summary>
    private long PlaceFamilyInstance()
    {
        _seed = FamilySeed.Create(Application, Doc, FamilyName, out var reason);
        if (_seed is null)
        {
            Skip.Test(reason ?? "Не удалось сидировать семейство");
            return 0;
        }

        long instanceId = 0;
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
                XYZ.Zero,
                symbol,
                StructuralType.NonStructural);
            instanceId = instance.Id.GetValue();
        });
        return instanceId;
    }

    private Element WallElement => Doc.GetElement(ElementIdCompat.Create(_wallId));

    private ElementType WallTypeOf() => (ElementType)Doc.GetElement(WallElement.GetTypeId());

    private ToolResult Query(string json) =>
        new QueryElementsTool(new StubRevitContext(Doc)).ExecuteForTests(Doc, Parse(json), CancellationToken.None);

    private ToolResult Summarize(string json) =>
        new SummarizeElementsTool(new StubRevitContext(Doc)).ExecuteForTests(Doc, Parse(json), CancellationToken.None);

    private static string WallsQuery(string conditions, string extra = "") =>
        "{\"categories\":[\"OST_Walls\"],\"conditions\":[" + conditions + "]" + extra + "}";

    private static string CategoriesQuery(string category, string conditions) =>
        "{\"categories\":[" + Json(category) + "],\"conditions\":[" + conditions + "],\"limit\":50}";

    private static string Condition(string parameter, string op, string value) =>
        ConditionRaw(parameter, op, Json(value));

    /// <summary>exists / notExists: the operator carries no value.</summary>
    private static string Condition(string parameter, string op) =>
        ConditionRaw(parameter, op, "null");

    private static string ConditionRaw(string parameter, string op, string valueJson, string extra = "") =>
        "{\"parameter\":" + Json(parameter) + ",\"op\":\"" + op + "\",\"value\":" + valueJson + extra + "}";

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static int Total(ToolResult result) => result.Data!.Value.GetProperty("total").GetInt32();

    /// <summary>Short verdict of a tool call for assertion messages.</summary>
    private static string Outcome(ToolResult result) =>
        result.Success ? "ok total=" + Total(result) : result.ErrorCode + ": " + result.Message;

    private static List<long> ElementIds(ToolResult result) =>
        result.Data!.Value.GetProperty("elements").EnumerateArray()
            .Select(card => card.GetProperty("elementId").GetInt64())
            .ToList();

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();

    private static List<JsonElement> Groups(ToolResult result) =>
        result.Data!.Value.GetProperty("groups").EnumerateArray().ToList();
}
