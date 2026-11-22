using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Read-туры связей (кейс 10 real-cases benchmark v2): get_element_relations,
/// trace_system_chain, get_room_contents. Сид: 2 комнаты, окна в известной
/// стене, дверь на границе комнат, MEP-ветвь с elbow-фитингом («кран») и
/// намеренным зазором ~7 мм между трубой B и трубой C («Т11/Т111»).
/// Revit-типы — только ленивые свойства/nullable-поля (правила IT #1, #7).
/// </summary>
public sealed partial class AgentRelationsToolsTests : RevitApiTest
{
    private const string OfficeRoomName = "SC_Офис 1";
    private const string OfficeRoomNumber = "101";
    private const string CorridorRoomName = "SC_Коридор";
    private const string SystemNameMain = "SC_Т11";
    private const string SystemNameSplit = "SC_Т111";

    // Зазор недосынекта: 0.023 ft = 7.01 мм.
    private const double PipeGapFt = 0.023;
    private const double MepElevation = -2.0;

    private Document? _document;
    private RevitTransactionService? _transactions;
    private bool _architectureSeeded;
    private bool _mepSeeded;
    private long _levelId;
    private long _southWallId;
    private long _windowId;
    private long _doorId;
    private long _officeRoomId;
    private long _corridorRoomId;
    private long _pipeAId;
    private long _pipeBId;
    private long _pipeCId;
    private long _elbowId;
    private string? _windowFamilyName;
    private string? _doorFamilyName;

    private Document Doc => _document!;
    private RevitTransactionService Transactions => _transactions!;

    private static XYZ Mep(double x) => new(x, 0, MepElevation);

    [Before(Test)]
    public void Seed()
    {
        // Дефолтный шаблон проекта НЕ содержит pipe-fitting семейств в routing
        // preferences (NewElbowFitting падает «failed to insert elbow») — сидим
        // весь мир в MEP-шаблоне установки; его отсутствие — только read-only
        // архитектурные проверки скипаются вместе с MEP (SampleFiles-паттерн).
        _document = SampleFiles.NewMepTemplateDocument(Application);
        if (_document is null)
            _document = Application.NewProjectDocument(UnitSystem.Metric);

        if (!ModelSeed.HasWallType(Doc) || !ModelSeed.HasPipeTypes(Doc))
            Skip.Test("В шаблоне проекта нет типов стен или труб — сидирование невозможно");

        var windowSymbol = FirstFamilySymbol(Doc, BuiltInCategory.OST_Windows);
        var doorSymbol = FirstFamilySymbol(Doc, BuiltInCategory.OST_Doors);

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));

        // Архитектура сеется всегда (MEP-шаблон без своих дверей/окон —
        // семейства создаются из family-шаблонов установки, FamilySeed-паттерн;
        // нет шаблонов — скип-гард архитектурных тестов).
        Transactions.RunInTransaction(Doc, "Seed rooms and openings", doc =>
        {
            windowSymbol ??= LoadSymbolFromFamilyTemplate(
                doc, "Metric Window.rft", "Метрическая система, окно.rft");
            doorSymbol ??= LoadSymbolFromFamilyTemplate(
                doc, "Metric Door.rft", "Метрическая система, дверь.rft");
            windowSymbol ??= LoadSymbolFromLibrary(doc, "Window", "Окно");
            doorSymbol ??= LoadSymbolFromLibrary(doc, "Door", "Дверь");
            if (windowSymbol is null || doorSymbol is null)
                return;

            try
            {
                SeedArchitecture(doc, windowSymbol, doorSymbol);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "PROBE AgentRelations arch seed failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        });

        SeedMepBranch();
    }

    private void SeedArchitecture(Document doc, FamilySymbol windowSymbol, FamilySymbol doorSymbol)
    {
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            _levelId = level.Id.GetValue();
            var wallTypeId = FirstTypeId(doc, typeof(WallType))!;

                var south = Wall.Create(doc,
                    Line.CreateBound(new XYZ(0, 0, 0), new XYZ(20, 0, 0)),
                    wallTypeId, level.Id, 10.0, 0.0, false, false);
                _ = Wall.Create(doc,
                    Line.CreateBound(new XYZ(0, 10, 0), new XYZ(20, 10, 0)),
                    wallTypeId, level.Id, 10.0, 0.0, false, false);
                _ = Wall.Create(doc,
                    Line.CreateBound(new XYZ(0, 0, 0), new XYZ(0, 10, 0)),
                    wallTypeId, level.Id, 10.0, 0.0, false, false);
                _ = Wall.Create(doc,
                    Line.CreateBound(new XYZ(20, 0, 0), new XYZ(20, 10, 0)),
                    wallTypeId, level.Id, 10.0, 0.0, false, false);
                var partition = Wall.Create(doc,
                    Line.CreateBound(new XYZ(10, 0, 0), new XYZ(10, 10, 0)),
                    wallTypeId, level.Id, 10.0, 0.0, false, false);
                doc.Regenerate();

                var office = doc.Create.NewRoom(level, new UV(5, 5));
                office?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(OfficeRoomName);
                office?.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.Set(OfficeRoomNumber);
                var corridor = doc.Create.NewRoom(level, new UV(15, 5));
                corridor?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(CorridorRoomName);
                corridor?.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.Set("102");
                doc.Regenerate();

                windowSymbol!.Activate();
                doorSymbol!.Activate();

                // Два окна в южной стене со стороны «Офиса 1», одно — со стороны «Коридора».
                var window = doc.Create.NewFamilyInstance(
                    new XYZ(5, 0, 3), windowSymbol, south, StructuralType.NonStructural);
                window.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("SC_О1");
                _ = doc.Create.NewFamilyInstance(
                    new XYZ(8, 0, 3), windowSymbol, south, StructuralType.NonStructural);
                _ = doc.Create.NewFamilyInstance(
                    new XYZ(12, 0, 3), windowSymbol, south, StructuralType.NonStructural);

                var door = doc.Create.NewFamilyInstance(
                    new XYZ(10, 4, 0), doorSymbol, partition, StructuralType.NonStructural);
                door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("SC_Д1");
                doc.Regenerate();

                _southWallId = south.Id.GetValue();
                _windowId = window.Id.GetValue();
                _doorId = door.Id.GetValue();
                _officeRoomId = office?.Id.GetValue() ?? 0;
                _corridorRoomId = corridor?.Id.GetValue() ?? 0;
                _windowFamilyName = windowSymbol.Family?.Name;
                _doorFamilyName = doorSymbol.Family?.Name;

            var officeRoom = Doc.GetElement(ElementIdCompat.Create(_officeRoomId)) as Room;
            var corridorRoom = Doc.GetElement(ElementIdCompat.Create(_corridorRoomId)) as Room;
            _architectureSeeded = officeRoom?.Location is not null
                               && corridorRoom?.Location is not null;
        }
    }

    /// <summary>
    /// MEP-ветвь: труба A —(elbow-колено 90° «кран»)— труба B ‖ зазор ~7 мм ‖ труба C.
    /// Трубы A и B перпендикулярны (NewElbowFitting не вставляется на
    /// коллинеарные трубы). Система ветки A-B переименована в SC_Т11,
    /// отдельная система трубы C — в SC_Т111 (модель реального «разрыва»).
    /// </summary>
    private void SeedMepBranch()
    {
        try
        {
            SeedMepBranchCore();
        }
        catch (Exception ex)
        {
            // Сид MEP-ветви зависит от шаблона (routing preferences, фитинги).
            // Недоступность — skip ветки MEP-тестов, а не падение всего класса.
            Console.WriteLine("PROBE AgentRelations: MEP seed failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void SeedMepBranchCore()
    {
        Transactions.RunInTransaction(Doc, "Seed MEP branch with gap", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            _levelId = level.Id.GetValue();
            var gapEnd = 10.0 + PipeGapFt;

            // Колено: A вдоль X к точке стыка, B от неё вдоль Y.
            var pipeA = ModelSeed.TryCreatePipe(
                doc, level.Id, new XYZ(0, 0, MepElevation), new XYZ(10, 0, MepElevation));
            var joint = new XYZ(10, 0, MepElevation);
            var pipeB = ModelSeed.TryCreatePipe(
                doc, level.Id, joint, new XYZ(10, 10, MepElevation));
            var pipeC = ModelSeed.TryCreatePipe(
                doc, level.Id,
                new XYZ(10, gapEnd, MepElevation),
                new XYZ(10, gapEnd + 10, MepElevation));
            if (pipeA is null || pipeB is null || pipeC is null)
                return;

            doc.Regenerate();
            var elbow = doc.Create.NewElbowFitting(
                ModelSeed.FindConnectorAt(pipeA, Mep(10)),
                ModelSeed.FindConnectorAt(pipeB, Mep(10)));
            elbow.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("SC_К1");
            doc.Regenerate();

            // Commit-регенерация сливает логические системы соединённых труб
            // одного типа (проверено PROBE) — после commit вся ветвь A-B в
            // одной системе SC_Т11; труба C осталась в отдельной SC_Т111.
            RenameSystem(doc, pipeA, SystemNameMain);
            RenameSystem(doc, pipeC, SystemNameSplit);
            doc.Regenerate();

            _pipeAId = pipeA.Id.GetValue();
            _pipeBId = pipeB.Id.GetValue();
            _pipeCId = pipeC.Id.GetValue();
            _elbowId = elbow.Id.GetValue();
            _mepSeeded = true;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task GetElementRelations_FittingHasTwoConnectedNeighbours()
    {
        if (!_mepSeeded)
            Skip.Test("В шаблоне проекта нет PipeType/PipingSystemType — MEP-сид невозможен");

        var tool = new GetElementRelationsTool(new StubRevitContext(Doc));
        var args = Parse("{\"elementIds\":[" + _elbowId + "]}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var relation = result.Data!.Value.GetProperty("relations")[0];
        await Assert.That(relation.GetProperty("elementId").GetInt64()).IsEqualTo(_elbowId);
        await Assert.That(relation.GetProperty("mark").GetString()).IsEqualTo("SC_К1");

        var connectors = relation.GetProperty("connectors");
        await Assert.That(connectors.GetArrayLength()).IsEqualTo(2);

        var neighbourIds = new HashSet<long>();
        foreach (var connector in connectors.EnumerateArray())
        {
            await Assert.That(connector.GetProperty("isConnected").GetBoolean()).IsTrue();
            foreach (var neighbor in connector.GetProperty("neighbors").EnumerateArray())
                neighbourIds.Add(neighbor.GetProperty("elementId").GetInt64());
        }

        await Assert.That(neighbourIds.Contains(_pipeAId)).IsTrue();
        await Assert.That(neighbourIds.Contains(_pipeBId)).IsTrue();
    }

    [Test]
    public async Task GetElementRelations_WindowHostIsSeededWall()
    {
        if (!_architectureSeeded)
            Skip.Test("В шаблоне нет семейств окон/дверей или комнаты не разместились");

        var tool = new GetElementRelationsTool(new StubRevitContext(Doc));
        var args = Parse("{\"elementIds\":[" + _windowId + "]}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var relation = result.Data!.Value.GetProperty("relations")[0];
        var host = relation.GetProperty("host");
        await Assert.That(host.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(host.GetProperty("elementId").GetInt64()).IsEqualTo(_southWallId);
        await Assert.That(host.GetProperty("category").GetString()).IsEqualTo("OST_Walls");

        // У окна нет MEP-коннекторов — пустой детерминированный массив.
        await Assert.That(relation.GetProperty("connectors").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task GetElementRelations_DoorFromAndToRoomsAreSeparateAndDifferent()
    {
        if (!_architectureSeeded)
            Skip.Test("В шаблоне нет семейств окон/дверей или комнаты не разместились");

        var tool = new GetElementRelationsTool(new StubRevitContext(Doc));
        var args = Parse("{\"elementIds\":[" + _doorId + "]}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var relation = result.Data!.Value.GetProperty("relations")[0];

        var fromRoom = relation.GetProperty("fromRoom");
        var toRoom = relation.GetProperty("toRoom");
        await Assert.That(fromRoom.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(toRoom.ValueKind).IsEqualTo(JsonValueKind.Object);

        var fromId = fromRoom.GetProperty("roomId").GetInt64();
        var toId = toRoom.GetProperty("roomId").GetInt64();
        await Assert.That(fromId).IsNotEqualTo(toId);

        var sideIds = new HashSet<long> { fromId, toId };
        await Assert.That(sideIds.Contains(_officeRoomId)).IsTrue();
        await Assert.That(sideIds.Contains(_corridorRoomId)).IsTrue();

        var names = new HashSet<string?>
        {
            fromRoom.GetProperty("name").GetString(),
            toRoom.GetProperty("name").GetString()
        };
        await Assert.That(names.Contains(OfficeRoomName)).IsTrue();
        await Assert.That(names.Contains(CorridorRoomName)).IsTrue();
    }

    [Test]
    public async Task GetElementRelations_BatchOfThree_ReturnsAllRelations()
    {
        if (!_mepSeeded || !_architectureSeeded)
            Skip.Test("Сид неполный (нет MEP-ветви или архитектуры) — batch-проверка невозможна");

        var tool = new GetElementRelationsTool(new StubRevitContext(Doc));
        var args = Parse("{\"elementIds\":[" + _windowId + "," + _doorId + "," + _elbowId + "]}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(3);
        await Assert.That(data.GetProperty("relations").GetArrayLength()).IsEqualTo(3);
        await Assert.That(data.GetProperty("missing").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task TraceSystemChain_TracesBranchAndFindsGapBreak()
    {
        if (!_mepSeeded)
            Skip.Test("В шаблоне проекта нет PipeType/PipingSystemType — MEP-сид невозможен");

        var tool = new TraceSystemChainTool(new StubRevitContext(Doc));
        var args = Parse("{\"elementId\":" + _pipeAId + "}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;

        // A — elbow — B: три элемента одной физической ветви.
        await Assert.That(data.GetProperty("chainLength").GetInt32()).IsEqualTo(3);
        await Assert.That(data.GetProperty("visitedCount").GetInt32()).IsEqualTo(3);
        await Assert.That(data.GetProperty("truncated").GetBoolean()).IsFalse();

        var chainIds = data.GetProperty("chain").EnumerateArray()
            .Select(node => node.GetProperty("elementId").GetInt64())
            .ToHashSet();
        await Assert.That(chainIds.Contains(_pipeAId)).IsTrue();
        await Assert.That(chainIds.Contains(_elbowId)).IsTrue();
        await Assert.That(chainIds.Contains(_pipeBId)).IsTrue();
        await Assert.That(chainIds.Contains(_pipeCId)).IsFalse();

        // Разрыв ровно один: конец B ↔ начало C, зазор 7 мм.
        var breaks = data.GetProperty("breaks");
        await Assert.That(breaks.GetArrayLength()).IsEqualTo(1);
        var gapBreak = breaks[0];
        var gapMm = gapBreak.GetProperty("gapMm").GetDouble();
        await Assert.That(gapMm).IsGreaterThanOrEqualTo(1.0);
        await Assert.That(gapMm).IsLessThanOrEqualTo(50.0);

        var breakIds = new HashSet<long>
        {
            gapBreak.GetProperty("fromElementId").GetInt64(),
            gapBreak.GetProperty("toElementId").GetInt64()
        };
        await Assert.That(breakIds.Contains(_pipeBId)).IsTrue();
        await Assert.That(breakIds.Contains(_pipeCId)).IsTrue();

        // Стороны разрыва несут имена систем «Т11» и «Т111»: сторона цепи —
        // система ветки A-B, внешняя сторона — отдельная система трубы C.
        var fromSystem = gapBreak.GetProperty("fromSystemName").GetString();
        var toSystem = gapBreak.GetProperty("toSystemName").GetString();
        await Assert.That(fromSystem).IsNotNull();
        await Assert.That(toSystem).IsEqualTo(SystemNameSplit);
        await Assert.That(string.Equals(fromSystem, toSystem, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task TraceSystemChain_SystemNameChangeAlongChain()
    {
        if (!_mepSeeded)
            Skip.Test("MEP-сид не выполнен (шаблон без фитингов) — проверка смены имени невозможна");

        // Соединение труб ОДНОГО типа систем сливает логические системы при
        // commit-регенерации (проверено PROBE: sysB после commit = SC_Т11).
        // Разные ТИПЫ систем физически соединяются без слияния — так
        // моделируется реальная ситуация «соединено, но имена разные».
        var secondSystemType = SecondPipingSystemTypeId(Doc);
        if (secondSystemType is null)
            Skip.Test("В шаблоне только один PipingSystemType — смену имени системы не смоделировать");

        long firstPipeId = 0;
        long secondPipeId = 0;
        Transactions.RunInTransaction(Doc, "Seed two-system chain", doc =>
        {
            var systemTypeId = FirstTypeId(doc, typeof(PipingSystemType))!;
            var pipeTypeId = FirstTypeId(doc, typeof(PipeType))!;
            var levelId = ElementIdCompat.Create(_levelId);

            var first = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId,
                new XYZ(35, 0, MepElevation), new XYZ(40, 0, MepElevation));
            var second = Pipe.Create(doc, secondSystemType!, pipeTypeId, levelId,
                new XYZ(40, 0, MepElevation), new XYZ(45, 0, MepElevation));
            doc.Regenerate();

            ModelSeed.FindConnectorAt(first, new XYZ(40, 0, MepElevation)).ConnectTo(
                ModelSeed.FindConnectorAt(second, new XYZ(40, 0, MepElevation)));
            doc.Regenerate();

            firstPipeId = first.Id.GetValue();
            secondPipeId = second.Id.GetValue();
        });

        var tool = new TraceSystemChainTool(new StubRevitContext(Doc));
        var args = Parse("{\"elementId\":" + firstPipeId + "}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("chainLength").GetInt32()).IsEqualTo(2);

        var changes = data.GetProperty("systemNameChanges");
        await Assert.That(changes.GetArrayLength()).IsEqualTo(1);
        var change = changes[0];
        await Assert.That(change.GetProperty("elementId").GetInt64()).IsEqualTo(secondPipeId);
        var before = change.GetProperty("before").GetString();
        var after = change.GetProperty("after").GetString();
        await Assert.That(before).IsNotNull();
        await Assert.That(after).IsNotNull();
        await Assert.That(string.Equals(before, after, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task GetRoomContents_OfficeGroupedByFamilyWithCounts()
    {
        if (!_architectureSeeded)
            Skip.Test("В шаблоне нет семейств окон/дверей или комнаты не разместились");

        var tool = new GetRoomContentsTool(new StubRevitContext(Doc));
        var args = Parse("{\"roomName\":\"" + OfficeRoomName + "\"}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;

        var room = data.GetProperty("room");
        await Assert.That(room.GetProperty("roomId").GetInt64()).IsEqualTo(_officeRoomId);
        await Assert.That(room.GetProperty("number").GetString()).IsEqualTo(OfficeRoomNumber);
        await Assert.That(room.GetProperty("areaM2").GetDouble()).IsGreaterThan(0);

        var contents = data.GetProperty("contents");
        var byFamily = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in contents.EnumerateArray())
        {
            var family = group.GetProperty("family").GetString();
            if (family is not null)
                byFamily[family] = group.GetProperty("count").GetInt32();
        }

        await Assert.That(byFamily.TryGetValue(_windowFamilyName!, out var windowCount)).IsTrue();
        await Assert.That(windowCount).IsGreaterThanOrEqualTo(2);
        await Assert.That(byFamily.TryGetValue(_doorFamilyName!, out var doorCount)).IsTrue();
        await Assert.That(doorCount).IsGreaterThanOrEqualTo(1);

        await Assert.That(data.GetProperty("familiesTotal").GetInt32()).IsGreaterThanOrEqualTo(2);
        await Assert.That(data.GetProperty("elementsTotal").GetInt32()).IsGreaterThanOrEqualTo(3);
        await Assert.That(data.GetProperty("truncated").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task GetRoomContents_ByNumberFindsSameRoom()
    {
        if (!_architectureSeeded)
            Skip.Test("В шаблоне нет семейств окон/дверей или комнаты не разместились");

        var tool = new GetRoomContentsTool(new StubRevitContext(Doc));
        var args = Parse("{\"roomNumber\":\"" + OfficeRoomNumber + "\"}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("room").GetProperty("roomId").GetInt64())
            .IsEqualTo(_officeRoomId);
    }

    [Test]
    public async Task GetRoomContents_WithoutAnySelector_Fails()
    {
        var tool = new GetRoomContentsTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

}
