using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Agent.Services.Tools;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 15 of the real-cases benchmark (v2 wave, SSOT case 10 «Аналитик
/// связей»): a read-only Q&amp;A over a seeded world — connector neighbours of
/// «К1» (в сиде — фитинг-колено с Mark «К1»: семейства из .rft не имеют
/// MEP-коннекторов, а NewElbowFitting даёт тот же ConnectorManager с двумя
/// трубами-соседями — механика соединений арматуры идентична), the host wall
/// of window «О1», from/to rooms of door «Д1», the family/quantity contents
/// of «Офис 1», the level of pump «Н1» (без коннекторов, размещён на
/// уровне), and the climax: a physically single branch that Revit sees as
/// two systems «Т11»/«Т111» because of a 7 mm gap between the elbow run
/// pipe and the next pipe. The seed reuses the AgentRelationsToolsTests
/// mechanics (MEP template, gap 0.023 ft, system renames, rooms via
/// NewRoom); the verdict (see Case15.Evaluation.cs) re-computes the
/// connector truth via ConnectorManager itself and accepts break ids from
/// the answer OR from trace_system_chain result JSON. Any non-dryRun write
/// call is a red flag. Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/real-cases-benchmark-v2.md, кейс 10).
    private const string Case15Text =
        "Разберись в связях моей модели и ответь на вопросы:\n" +
        "1. Шаровый кран «К1» — с какими элементами он физически соединён? Что подходит " +
        "к его входу и выходу?\n" +
        "2. Окно «О1» — в какой стене оно установлено?\n" +
        "3. Дверь «Д1» — из какого помещения в какое она ведёт?\n" +
        "4. Что стоит в помещении «Офис 1»? Перечисли состав по семействам с количеством.\n" +
        "5. На каком уровне установлен насос «Н1»?\n" +
        "6. Главное: ветка выглядит как одна физическая цепь, но Revit видит две системы — " +
        "«Т11» и «Т111». Пройди цепь от насоса и найди, ГДЕ и на каких элементах она " +
        "рвётся — почему имя системы не переходит дальше?";

    private const string Case15Title = "Аналитик связей модели (коннекторы, хосты, помещения, разрыв)";

    private const string Case15LevelName = "Этаж 1";
    private const string Case15OfficeName = "Офис 1";
    private const string Case15CorridorName = "Коридор";
    private const string Case15Room2Name = "Комната 2";
    private const string Case15SystemMain = "Т11";
    private const string Case15SystemSplit = "Т111";

    /// <summary>Зазор недосынекта: 0.023 ft = 7.01 мм (механика AgentRelationsToolsTests).</summary>
    private const double Case15PipeGapFt = 0.023;

    private const double Case15MepZ = -2.0;

    // Ни одного Revit-типа в полях (правило 1): вся истина сида — long/строки.
    private sealed class Case15World
    {
        public long Level1Id { get; set; }

        public long Level2Id { get; set; }

        public long SouthWallId { get; set; }

        public long WindowId { get; set; }

        public long DoorId { get; set; }

        public long OfficeRoomId { get; set; }

        public long CorridorRoomId { get; set; }

        public long Room2Id { get; set; }

        public long PumpId { get; set; }

        public long ValveId { get; set; }

        public long PipeAId { get; set; }

        public long PipeBId { get; set; }

        public long PipeB2Id { get; set; }

        public long ElbowId { get; set; }

        public long PipeCId { get; set; }

        public long PipeC2Id { get; set; }

        public string? MainSystemName { get; set; }

        public string? SplitSystemName { get; set; }
    }

    private Case15World _case15 = null!;

    private Dictionary<string, int> _case15OfficeContents = null!;

    private string? _case15SeedError;

    [Test]
    public async Task Case15_Live()
    {
        if (!LiveConfigured())
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        SeedOnBenchBaseLive(15, Case15Title, SeedCase15);
        var run = RunProductionCase(
            15, Case15Title, Case15Text,
            Case15SeedSummary(), EvaluateCase15, DumpCase15State);

        // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
        var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
        await Assert.That(runHappened).IsTrue();
    }

    /// <summary>Гейт сида для ночных прогонов без LLM: структура сида без live-гарда.</summary>
    [Test]
    public async Task Case15_SeedVerifies()
    {
        SeedOnBenchBase("case15", SeedCase15);

        await Assert.That(_case15.OfficeRoomId > 0).IsTrue();
        await Assert.That(_case15.CorridorRoomId > 0).IsTrue();
        await Assert.That(_case15.WindowId > 0).IsTrue();
        await Assert.That(_case15.DoorId > 0).IsTrue();
        await Assert.That(_case15.PumpId > 0).IsTrue();
        await Assert.That(_case15.ValveId > 0).IsTrue();

        var neighbours = Case15ValveNeighbours();
        await Assert.That(neighbours.Contains(_case15.PipeAId)).IsTrue();
        await Assert.That(neighbours.Contains(_case15.PipeBId)).IsTrue();

        var window = Doc.GetElement(ElementIdCompat.Create(_case15.WindowId)) as FamilyInstance;
        await Assert.That(window?.Host?.Id.GetValue() == _case15.SouthWallId).IsTrue();

        var breaks = Case15TraceBreaks(out var chainLength);
        await Assert.That(chainLength).IsGreaterThanOrEqualTo(4);
        await Assert.That(breaks.Count).IsEqualTo(1);
        var pair = new HashSet<long> { breaks[0].From, breaks[0].To };
        await Assert.That(pair.Contains(_case15.PipeB2Id)).IsTrue();
        await Assert.That(pair.Contains(_case15.PipeCId)).IsTrue();

        await Assert.That(_case15.MainSystemName ?? string.Empty).IsEqualTo(Case15SystemMain);
        await Assert.That(_case15.SplitSystemName ?? string.Empty).IsEqualTo(Case15SystemSplit);
        await Assert.That(_case15OfficeContents.Count).IsGreaterThanOrEqualTo(2);
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase15()
    {
        // MEP-шаблон установки: нужны типы стен, труб и фитинги в routing
        // preferences (паттерн AgentRelationsToolsTests); окна/двери/насос —
        // из family-шаблонов, не из библиотеки .rfa.
        _document = SampleFiles.NewMepTemplateDocument(Application);
        _document ??= BenchDocuments.NewBase(Application);
        if (!ModelSeed.HasWallType(Doc) || !ModelSeed.HasPipeTypes(Doc))
        {
            Skip.Test("В шаблоне проекта нет типов стен или труб — сид кейса 15 невозможен");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _case15 = new Case15World();
        _case15SeedError = null;

        _transactions.RunInTransaction(Doc, "Seed case 15 (architecture)", doc =>
        {
            try
            {
                SeedCase15Architecture(doc);
            }
            catch (Exception ex)
            {
                _case15SeedError = "Архитектурный сид кейса 15: " + ex.GetType().Name + ": " + ex.Message;
            }
        });

        if (_case15SeedError is not null)
        {
            Skip.Test(_case15SeedError);
            return;
        }

        _transactions.RunInTransaction(Doc, "Seed case 15 (MEP branch with gap)", doc =>
        {
            try
            {
                SeedCase15Mep(doc);
            }
            catch (Exception ex)
            {
                _case15SeedError = "MEP-сид кейса 15: " + ex.GetType().Name + ": " + ex.Message;
            }
        });

        if (_case15SeedError is not null)
        {
            Skip.Test(_case15SeedError);
            return;
        }

        VerifyCase15Seed();
    }

    private void SeedCase15Architecture(Document doc)
    {
        var level1 = ModelSeed.CreateLevel(doc, 0.0);
        level1.Name = Case15LevelName;
        var level2 = ModelSeed.CreateLevel(doc, 10.0);
        level2.Name = "Этаж 2";
        _case15.Level1Id = level1.Id.GetValue();
        _case15.Level2Id = level2.Id.GetValue();

        // Семейства создаются из family-шаблонов установки (библиотека .rfa
        // машины MEP-семейств не содержит): имя файла .rfa = имя семейства.
        var windowFamily = LoadBenchFamilyFromTemplate(
            doc, "Окно", new[] { "ОР 15-12" },
            "Metric Window.rft", "Метрическая система, окно.rft");
        var doorFamily = LoadBenchFamilyFromTemplate(
            doc, "Дверь", new[] { "ДГ 21-7" },
            "Metric Door.rft", "Метрическая система, дверь.rft");
        var pumpFamily = LoadBenchFamilyFromTemplate(
            doc, "Насос", new[] { "К 20-30" }, "Metric Mechanical Equipment.rft");
        var windowSymbol = windowFamily is null ? null : BenchFirstSymbolOf(windowFamily, doc);
        var doorSymbol = doorFamily is null ? null : BenchFirstSymbolOf(doorFamily, doc);
        var pumpSymbol = pumpFamily is null ? null : BenchFirstSymbolOf(pumpFamily, doc);
        if (windowSymbol is null || doorSymbol is null)
        {
            _case15SeedError = "Не удалось создать семейства окон/дверей из family-шаблонов установки";
            return;
        }

        if (pumpSymbol is null)
        {
            _case15SeedError = "Не удалось создать семейство насоса из шаблона Metric Mechanical Equipment.rft";
            return;
        }

        var wallTypeId = BenchFirstTypeId(doc, typeof(WallType));
        if (wallTypeId is null)
        {
            _case15SeedError = "Нет типа стен после проверки шаблона";
            return;
        }

        var south = Wall.Create(doc,
            Line.CreateBound(new XYZ(0, 0, 0), new XYZ(20, 0, 0)),
            wallTypeId, level1.Id, 10.0, 0.0, false, false);
        _ = Wall.Create(doc,
            Line.CreateBound(new XYZ(0, 10, 0), new XYZ(20, 10, 0)),
            wallTypeId, level1.Id, 10.0, 0.0, false, false);
        _ = Wall.Create(doc,
            Line.CreateBound(new XYZ(0, 0, 0), new XYZ(0, 10, 0)),
            wallTypeId, level1.Id, 10.0, 0.0, false, false);
        _ = Wall.Create(doc,
            Line.CreateBound(new XYZ(20, 0, 0), new XYZ(20, 10, 0)),
            wallTypeId, level1.Id, 10.0, 0.0, false, false);
        var partition1 = Wall.Create(doc,
            Line.CreateBound(new XYZ(10, 0, 0), new XYZ(10, 10, 0)),
            wallTypeId, level1.Id, 10.0, 0.0, false, false);
        var partition2 = Wall.Create(doc,
            Line.CreateBound(new XYZ(15, 0, 0), new XYZ(15, 10, 0)),
            wallTypeId, level1.Id, 10.0, 0.0, false, false);
        doc.Regenerate();

        var office = doc.Create.NewRoom(level1, new UV(5, 5));
        office?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(Case15OfficeName);
        office?.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.Set("101");
        var corridor = doc.Create.NewRoom(level1, new UV(12.5, 5));
        corridor?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(Case15CorridorName);
        corridor?.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.Set("102");
        var room2 = doc.Create.NewRoom(level1, new UV(17.5, 5));
        room2?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(Case15Room2Name);
        room2?.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.Set("103");
        doc.Regenerate();

        windowSymbol.Activate();
        doorSymbol.Activate();
        pumpSymbol.Activate();

        var window = doc.Create.NewFamilyInstance(
            new XYZ(5, 0, 3), windowSymbol, south, StructuralType.NonStructural);
        window.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("О1");
        _ = doc.Create.NewFamilyInstance(
            new XYZ(8, 0, 3), windowSymbol, south, StructuralType.NonStructural);
        _ = doc.Create.NewFamilyInstance(
            new XYZ(12, 0, 3), windowSymbol, south, StructuralType.NonStructural);
        _ = doc.Create.NewFamilyInstance(
            new XYZ(17, 0, 3), windowSymbol, south, StructuralType.NonStructural);

        var door = doc.Create.NewFamilyInstance(
            new XYZ(10, 4, 0), doorSymbol, partition1, StructuralType.NonStructural);
        door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("Д1");
        var door2 = doc.Create.NewFamilyInstance(
            new XYZ(15, 4, 0), doorSymbol, partition2, StructuralType.NonStructural);
        door2.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("Д2");

        // Насос в «Офисе 1»: level-based, иначе беспризорный fallback.
        FamilyInstance? pump = null;
        var pumpPoint = new XYZ(3, 7, 0);
        try
        {
            pump = doc.Create.NewFamilyInstance(
                pumpPoint, pumpSymbol, level1, StructuralType.NonStructural);
        }
        catch (Exception)
        {
            pump = null;
        }

        if (pump is null)
        {
            pump = doc.Create.NewFamilyInstance(
                pumpPoint, pumpSymbol, StructuralType.NonStructural);
        }

        pump.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("Н1");
        doc.Regenerate();

        _case15.SouthWallId = south.Id.GetValue();
        _case15.WindowId = window.Id.GetValue();
        _case15.DoorId = door.Id.GetValue();
        _case15.OfficeRoomId = office?.Id.GetValue() ?? 0;
        _case15.CorridorRoomId = corridor?.Id.GetValue() ?? 0;
        _case15.Room2Id = room2?.Id.GetValue() ?? 0;
        _case15.PumpId = pump.Id.GetValue();
    }

    /// <summary>
    /// MEP-ветвь: труба A —(фитинг-кран «К1», колено)— труба B —(отвод «Ф1»,
    /// колено)— труба B2 ‖ зазор 7 мм ‖ труба C — труба C2. Ветвь A…B2 в
    /// системе «Т11», дальняя сторона C — «Т111». Семейства из .rft не имеют
    /// MEP-коннекторов, поэтому «кран К1» — колено NewElbowFitting: тот же
    /// ConnectorManager, что у трубной арматуры, соседи — трубы A и B
    /// (механика AgentRelationsToolsTests: колено вставляется на
    /// неколлинеарных трубах).
    /// </summary>
    private void SeedCase15Mep(Document doc)
    {
        var systemTypeId = BenchFirstTypeId(doc, typeof(PipingSystemType));
        var pipeTypeId = BenchFirstTypeId(doc, typeof(PipeType));
        if (systemTypeId is null || pipeTypeId is null)
        {
            _case15SeedError = "PipeType/PipingSystemType исчезли после проверки шаблона";
            return;
        }

        var levelId = ElementIdCompat.Create(_case15.Level1Id);
        var joint1 = new XYZ(10, 5, Case15MepZ);
        var joint2 = new XYZ(10, 15, Case15MepZ);
        var end2 = new XYZ(20, 15, Case15MepZ);

        var pipeA = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId,
            new XYZ(0, 5, Case15MepZ), joint1);
        var pipeB = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, joint1, joint2);
        doc.Regenerate();
        var valve = doc.Create.NewElbowFitting(
            ModelSeed.FindConnectorAt(pipeA, joint1),
            ModelSeed.FindConnectorAt(pipeB, joint1));
        valve.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("К1");
        doc.Regenerate();

        var pipeB2 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, joint2, end2);
        doc.Regenerate();
        var elbow = doc.Create.NewElbowFitting(
            ModelSeed.FindConnectorAt(pipeB, joint2),
            ModelSeed.FindConnectorAt(pipeB2, joint2));
        elbow.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("Ф1");
        doc.Regenerate();

        var gapEnd = new XYZ(20 + Case15PipeGapFt, 15, Case15MepZ);
        var jointC = new XYZ(28 + Case15PipeGapFt, 15, Case15MepZ);
        var pipeC = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, gapEnd, jointC);
        var pipeC2 = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, jointC,
            new XYZ(36 + Case15PipeGapFt, 15, Case15MepZ));
        doc.Regenerate();
        ModelSeed.FindConnectorAt(pipeC, jointC).ConnectTo(ModelSeed.FindConnectorAt(pipeC2, jointC));
        doc.Regenerate();

        // Системы: соединённая ветвь — «Т11», дальняя сторона разрыва — «Т111»
        // (механика AgentRelationsToolsTests: commit-регенерация сливает ветвь).
        var mainSystemId = pipeA.MEPSystem?.Id;
        var splitSystemId = pipeC.MEPSystem?.Id;
        if (mainSystemId is null || splitSystemId is null || mainSystemId.Equals(splitSystemId))
        {
            _case15SeedError = "Логические системы ветви и дальнего участка не разделились";
            return;
        }

        Case15RenameElement(doc.GetElement(mainSystemId), Case15SystemMain);
        Case15RenameElement(doc.GetElement(splitSystemId), Case15SystemSplit);
        doc.Regenerate();

        // C10-06: текст кейса называет «К1» шаровым краном. Семейства из .rft
        // без MEP-коннекторов не соединят трубы, поэтому соединительную
        // механику даёт колено NewElbowFitting (тот же ConnectorManager, что
        // у арматуры), а смена типа фитинга на дубликат «Шаровый кран»
        // РАЗОРВАЛА коннекторы (проба 2026-11-23: соседи К1 пусты) — тип не
        // меняется, вопрос 1 работает по марке К1, истина инвариантна имени
        // семейства. Настоящий дефект C10-06 — дубль марки К1 на CenterLine
        // элементе шаблона — лечится ниже снятием дублей.

        _case15.ValveId = valve.Id.GetValue();
        _case15.PipeAId = pipeA.Id.GetValue();
        _case15.PipeBId = pipeB.Id.GetValue();
        _case15.PipeB2Id = pipeB2.Id.GetValue();
        _case15.ElbowId = elbow.Id.GetValue();
        _case15.PipeCId = pipeC.Id.GetValue();
        _case15.PipeC2Id = pipeC2.Id.GetValue();

        // C10-06: на базе реального шаблона встречаются элементы/субэлементы
        // (например CenterLine) с теми же марками — вопрос 1 становится
        // двусмысленным (query по «К1» находил дубль). Чужие носители марок
        // сида очищаются.
        Case15DedupeMarks(doc);
    }

    /// <summary>
    /// Элементы-носители марки (ALL_MODEL_MARK == mark, не типы), КРОМЕ
    /// графических линий осей (Center line): осевая линия MEP-фитинга —
    /// проекция самого фитинга, её Mark синхронизируется Revit'ом и не
    /// снимается (проба 2026-11-23: cleared → восстановлен). Перебор
    /// коллектора без ElementParameterFilter: конструкторы FilterStringRule
    /// менялись между версиями API, а сид обязан собираться под R21-R26.
    /// </summary>
    private static List<Element> Case15MarkHolders(Document doc, string mark)
    {
        try
        {
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .Where(element => !Case15IsCenterLineElement(element))
                .Where(element =>
                {
                    try
                    {
                        return string.Equals(
                            element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            mark,
                            StringComparison.Ordinal);
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                })
                .ToList();
        }
        catch (Exception)
        {
            // Пустой список — VerifyCase15Seed сообщит о неуникальных марках
            // или сид не соберётся.
            return new List<Element>();
        }
    }

    private static bool Case15IsCenterLineElement(Element element)
    {
        try
        {
            var category = element.Category;
            while (category is not null)
            {
                if (category.Id.GetValue() == (long)BuiltInCategory.OST_Lines)
                    return true;

                var name = category.Name ?? string.Empty;
                if (name.IndexOf("center", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("осев", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                category = category.Parent;
            }
        }
        catch (Exception)
        {
            // Категория недоступна — считать обычным элементом.
        }

        return false;
    }

    /// <summary>
    /// Снимает марки сида с чужих элементов (дубли марок шаблона). Осевые
    /// линии (Center line) пропускаются: их Mark восстанавливается Revit'ом.
    /// </summary>
    private void Case15DedupeMarks(Document doc)
    {
        var keep = new HashSet<long>
        {
            _case15.ValveId, _case15.WindowId, _case15.DoorId,
            _case15.PumpId, _case15.ElbowId
        };
        foreach (var mark in new[] { "К1", "О1", "Д1", "Н1", "Ф1" })
        {
            foreach (var holder in Case15MarkHolders(doc, mark))
            {
                if (keep.Contains(holder.Id.GetValue()))
                    continue;

                try
                {
                    holder.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(string.Empty);
                }
                catch (Exception)
                {
                    // Не снимается — VerifyCase15Seed честно скипнет по
                    // неуникальной марке (вопрос кейса двусмысленен).
                }
            }
        }
    }

    private static void Case15RenameElement(Element? element, string name)
    {
        if (element is null)
            return;

        try
        {
            element.Name = name;
        }
        catch (ArgumentException)
        {
            // Отказ переименования tolerated: VerifyCase15Seed сравнит
            // фактические имена систем с ожидаемыми.
        }
    }

    // ── Сводка сида и тулы ──────────────────────────────────────────────
    // VerifyCase15Seed / истина связей / DumpCase15State — в
    // AgentRealCasesBenchmarkTests.Case15.State.cs; вердикт — в Case15.Evaluation.cs.

    private List<string> Case15SeedSummary()
    {
        return new List<string>
        {
            "2 уровня («" + Case15LevelName + "», «Этаж 2»), 6 стен, 3 помещения: «" +
            Case15OfficeName + "» (101), «" + Case15CorridorName + "» (102), «" +
            Case15Room2Name + "» (103)",
            "4 окна в южной стене; «О1» = id " + _case15.WindowId + ", стена id " + _case15.SouthWallId,
            "Двери «Д1» (Офис 1 ↔ Коридор, id " + _case15.DoorId + ") и «Д2» (Коридор ↔ Комната 2)",
            "Насос «Н1» id " + _case15.PumpId + " в «Офисе 1», уровень «" + Case15LevelName +
            "» (семейство из шаблона, без трубных коннекторов)",
            "MEP-ветвь: фитинг-кран «К1» (колено, Mark) id " + _case15.ValveId +
            " между трубами A/B + отвод «Ф1» id " + _case15.ElbowId +
            "; зазор 7 мм между трубой B2 (id " + _case15.PipeB2Id + ") и трубой C (id " + _case15.PipeCId + ")",
            "Системы: ветвь = «" + _case15.MainSystemName + "», дальняя сторона = «" +
            _case15.SplitSystemName + "»",
            "Состав «Офис 1»: " + string.Join("; ",
                _case15OfficeContents.Select(pair => pair.Key + " ×" + pair.Value))
        };
    }

}
