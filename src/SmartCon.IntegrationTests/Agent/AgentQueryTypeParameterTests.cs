using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Query conditions against TYPE-level parameter values (C5-05/C8-07): a value
/// written with set_parameters scope="type" must be findable by a condition with
/// includeTypeParameters=true, and notExists must see the empty type-bound
/// parameter. Also pins the element card shape: fields=["type"] carries the type
/// NAME (C10-05) and the room card does not glue name and number (C10-01).
/// </summary>
public sealed class AgentQueryTypeParameterTests : RevitApiTest
{
    private const string RoomName = "SC Офис";
    private const string RoomNumber = "101";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private bool _wallCreated;
    private long _wallId;
    private long _wallTypeId;
    private long _roomId;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _transactions.RunInTransaction(Doc, "Seed type-parameter query model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            var wall = ModelSeed.TryCreateWall(doc, level.Id);
            _wallCreated = wall is not null;
            _wallId = wall?.Id.GetValue() ?? 0;
            _wallTypeId = wall?.GetTypeId().GetValue() ?? 0;

            var room = doc.Create.NewRoom(level, new UV(5, 5));
            room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(RoomName);
            room?.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.Set(RoomNumber);
            _roomId = room?.Id.GetValue() ?? 0;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task QueryElements_TypeBuiltInParameter_AfterTypeScopeWrite_IsFindable()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        const string code = "30.10.90";
        // The code is dictated by the user verbatim — the documented way to write
        // an Assembly Code with no classification table loaded.
        var write = WriteParameter(
            "{\"elementId\":" + _wallId + ",\"parameter\":\"" +
            AgentParameterResolver.AssemblyCodeBuiltIn +
            "\",\"value\":\"" + code + "\",\"scope\":\"type\"}",
            ",\"allowUnverifiedClassification\":true");
        await Assert.That(write.Success).IsTrue();

        // The condition names the localized display name of the built-in, the way
        // the model sees it in card payloads — not the enum id.
        var displayName = Doc.GetElement(ElementIdCompat.Create(_wallTypeId))!
            .get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn)!
            .Definition!.Name;

        var result = Query(
            "{\"categories\":[\"OST_Walls\"],\"conditions\":[{\"parameter\":" + Json(displayName) +
            ",\"op\":\"in\",\"value\":[\"" + code + "\"]}],\"includeTypeParameters\":true,\"limit\":10}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Total(result)).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task QueryElements_TypeBuiltInParameter_EqOperator_WithIncludeTypeParameters()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        const string code = "31.11.11";
        var write = WriteParameter(
            "{\"elementId\":" + _wallId + ",\"parameter\":\"" +
            AgentParameterResolver.AssemblyCodeBuiltIn +
            "\",\"value\":\"" + code + "\",\"scope\":\"type\"}",
            ",\"allowUnverifiedClassification\":true");
        await Assert.That(write.Success).IsTrue();

        var displayName = Doc.GetElement(ElementIdCompat.Create(_wallTypeId))!
            .get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn)!
            .Definition!.Name;

        var result = Query(
            "{\"categories\":[\"OST_Walls\"],\"conditions\":[{\"parameter\":" + Json(displayName) +
            ",\"op\":\"eq\",\"value\":\"" + code + "\"}],\"includeTypeParameters\":true,\"limit\":10}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Total(result)).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task QueryElements_NotExists_OnEmptyTypeBoundParameter_FindsElements()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        // ALL_MODEL_TYPE_COMMENTS lives on the type only and is empty on a fresh
        // project: notExists with includeTypeParameters=true must see that hole.
        var empty = Query(
            "{\"categories\":[\"OST_Walls\"],\"conditions\":[{\"parameter\":\"ALL_MODEL_TYPE_COMMENTS\"," +
            "\"op\":\"notExists\"}],\"includeTypeParameters\":true,\"limit\":10}");

        await Assert.That(empty.Success).IsTrue();
        await Assert.That(Total(empty)).IsGreaterThanOrEqualTo(1);

        var write = WriteParameter(
            "{\"elementId\":" + _wallId + ",\"parameter\":\"ALL_MODEL_TYPE_COMMENTS\"," +
            "\"value\":\"SC seeded\",\"scope\":\"type\"}");
        await Assert.That(write.Success).IsTrue();

        var filled = Query(
            "{\"categories\":[\"OST_Walls\"],\"conditions\":[{\"parameter\":\"ALL_MODEL_TYPE_COMMENTS\"," +
            "\"op\":\"notExists\"}],\"includeTypeParameters\":true,\"limit\":10}");
        await Assert.That(filled.Success).IsTrue();
        await Assert.That(Total(filled)).IsEqualTo(0);

        var exists = Query(
            "{\"categories\":[\"OST_Walls\"],\"conditions\":[{\"parameter\":\"ALL_MODEL_TYPE_COMMENTS\"," +
            "\"op\":\"exists\"}],\"includeTypeParameters\":true,\"limit\":10}");
        await Assert.That(exists.Success).IsTrue();
        await Assert.That(Total(exists)).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task QueryElements_FieldsType_ReturnsTypeName()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var typeName = Doc.GetElement(ElementIdCompat.Create(_wallTypeId))!.Name;

        var result = Query(
            "{\"categories\":[\"OST_Walls\"],\"fields\":[\"elementId\",\"type\"],\"limit\":10}");

        await Assert.That(result.Success).IsTrue();
        var elements = result.Data!.Value.GetProperty("elements").EnumerateArray().ToList();
        await Assert.That(elements.Count).IsGreaterThanOrEqualTo(1);
        var wall = elements.First(card => card.GetProperty("elementId").GetInt64() == _wallId);
        await Assert.That(wall.GetProperty("type").GetString()).IsEqualTo(typeName);
    }

    [Test]
    public async Task QueryElements_RoomNameCondition_MatchesRoomNameWithoutNumber()
    {
        if (_roomId == 0)
            Skip.Test("Комната не создана");

        var byName = Query(
            "{\"categories\":[\"OST_Rooms\"],\"conditions\":[{\"parameter\":\"name\",\"op\":\"eq\",\"value\":" +
            Json(RoomName) + "}],\"limit\":10}");

        await Assert.That(byName.Success).IsTrue();
        await Assert.That(Total(byName)).IsGreaterThanOrEqualTo(1);

        // The card must not glue the name and the number into one string with no
        // separator ("SC Офис101"): the name stays the room name.
        var elements = byName.Data!.Value.GetProperty("elements").EnumerateArray().ToList();
        var roomCard = elements.First(card => card.GetProperty("elementId").GetInt64() == _roomId);
        var cardName = roomCard.GetProperty("name").GetString() ?? string.Empty;
        await Assert.That(cardName.Contains(RoomName, StringComparison.Ordinal)).IsTrue();
        await Assert.That(string.Equals(cardName, RoomName + RoomNumber, StringComparison.Ordinal))
            .IsFalse();
    }

    [Test]
    public async Task QueryElements_RoomCard_SeparatesNameAndNumber()
    {
        if (_roomId == 0)
            Skip.Test("Комната не создана");

        var result = Query("{\"categories\":[\"OST_Rooms\"],\"limit\":10}");

        await Assert.That(result.Success).IsTrue();
        var elements = result.Data!.Value.GetProperty("elements").EnumerateArray().ToList();
        var roomCard = elements.First(card => card.GetProperty("elementId").GetInt64() == _roomId);
        var cardName = roomCard.GetProperty("name").GetString() ?? string.Empty;
        // Either the clean room name or "name (number)" — never the glued form.
        await Assert.That(string.Equals(cardName, RoomName, StringComparison.Ordinal)
                         || string.Equals(cardName, RoomName + " (" + RoomNumber + ")", StringComparison.Ordinal))
            .IsTrue();
    }

    [Test]
    public async Task QueryElements_NoMatchesOnTypeBoundParameter_ExplainsTypeBinding()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        // ALL_MODEL_TYPE_MARK lives on the wall type only and is empty here: an
        // eq condition matches nothing, and the honest answer says where the
        // parameter actually lives instead of a bare "Matched 0" (C9-03).
        var result = Query(
            "{\"categories\":[\"OST_Walls\"],\"conditions\":[{\"parameter\":\"ALL_MODEL_TYPE_MARK\"," +
            "\"op\":\"eq\",\"value\":\"zz-no-such-mark\"}],\"includeTypeParameters\":true,\"limit\":10}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Total(result)).IsEqualTo(0);

        var hint = result.Data!.Value.GetProperty("typeBoundParameter");
        await Assert.That(hint.GetProperty("types").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(ContainsText(result, "is bound to element types")).IsTrue();
        await Assert.That(ContainsText(result, "find_families_with_parameter")).IsTrue();
    }

    [Test]
    public async Task SummarizeElements_NoMatchesOnTypeBoundParameter_ExplainsTypeBinding()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = new SummarizeElementsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse(
                "{\"categories\":[\"OST_Walls\"],\"conditions\":[{\"parameter\":\"ALL_MODEL_TYPE_MARK\"," +
                "\"op\":\"eq\",\"value\":\"zz-no-such-mark\"}],\"includeTypeParameters\":true," +
                "\"groupBy\":\"category\"}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("total").GetInt32()).IsEqualTo(0);
        var hint = result.Data.Value.GetProperty("typeBoundParameter");
        await Assert.That(hint.GetProperty("types").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(ContainsText(result, "is bound to element types")).IsTrue();
    }

    private static bool ContainsText(ToolResult result, string fragment) =>
        (result.Message ?? string.Empty).Contains(fragment, StringComparison.Ordinal);

    private ToolResult WriteParameter(string itemJson, string extraArgs = "") =>
        new SetParametersTool(new StubRevitContext(Doc), _transactions!, new InMemoryAgentChangeSetStore())
            .ExecuteForTests(
                Parse("{\"items\":[" + itemJson + "],\"dryRun\":false" + extraArgs + "}"),
                approved: true,
                CancellationToken.None);

    private ToolResult Query(string json) =>
        new QueryElementsTool(new StubRevitContext(Doc)).ExecuteForTests(Doc, Parse(json), CancellationToken.None);

    private static int Total(ToolResult result) => result.Data!.Value.GetProperty("total").GetInt32();

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
