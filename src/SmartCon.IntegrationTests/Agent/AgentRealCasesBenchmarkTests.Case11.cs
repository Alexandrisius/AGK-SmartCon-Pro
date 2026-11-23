using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 11 of the real-cases benchmark = v2 case 6 (sheets + explication):
/// the model already has per-system 3D views; the agent must assemble an
/// album continuation — per system a new sheet with the viewport in the
/// top-left corner INSIDE the border, an explication (a filtered copy of the
/// shared 9-column specification: filter «АДСК_ИмяСистемы = система листа»,
/// only Наименование/Ед.изм./Количество visible) placed to the right, and a
/// paper format chosen through the title block's editable «Кратность размера»
/// (the long Т1 route needs a bigger sheet or an explicit justification).
/// СИД НАСЛЕДУЕТ КЕЙС 4 v2 (его «результат»): 3 чистые системы «Т1»/«Т2»/
/// «Т1.11» (Case15-механика труб, отдельные PipingSystemType на систему),
/// «АДСК_ИмяСистемы» заполнен ВСЕМ элементам включая фитинги, у «Т1» —
/// трасса 35+ м. Фильтры на 3D-видах не ставятся (кейс их «уже сделанными»
/// считает, вердикт не наблюдает). Сид — в партиале Case11.Seed, вердикт —
/// в партиале Case11.Evaluation (EvaluateCase11). Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/real-cases-benchmark-v2.md, кейс 6).
    private const string Case11Text =
        "У меня в модели трубопроводные системы, по каждой уже сделан 3D-вид с " +
        "фильтром. Для выпуска документации собери листы: на каждый 3D-вид — " +
        "отдельный лист. Вид размести в левом верхнем углу листа, полностью внутри " +
        "его границ — ничего не должно торчать. Справа от вида размести экспликацию " +
        "этой системы: узкая таблица — Наименование, Единицы измерения, Количество " +
        "(артикул и остальные широкие колонки общей спецификации не нужны). Экспликацию " +
        "собери на основе общей спецификации проекта, но с фильтром по общему " +
        "параметру «АДСК_ИмяСистемы», равному имени системы, — именно по нему, а не " +
        "по системному имени, потому что вложенные общие семейства (фланцы) несут " +
        "только его. Формат листа подбирай по размеру вида: у титульного блока есть " +
        "редактируемый параметр кратности размера — большая система должна попасть " +
        "на больший формат. Новые листы — в конец альбома, с продолжением нумерации " +
        "и краткими инженерными именами.";

    private const string Case11Title = "Листы под системные 3D-виды + экспликация";

    private const string Case11MainScheduleName = "Общая спецификация систем";

    /// <summary>Редактируемый параметр титульного семейства (владельческая конвенция кейса).</summary>
    private const string Case11TitleblockMultiplicity = "Кратность размера";

    private const string Case11SharedGroup = "СК_Кейс11";

    /// <summary>Общий параметр имени системы (конвенция проекта — даётся кейсом).</summary>
    private const string Case11SystemParameterName = "АДСК_ИмяСистемы";

    /// <summary>Чистые системы кейса 4 v2; «Т1» — префикс «Т1.11» (сопоставление с длинного).</summary>
    private static readonly string[] Case11Systems = { "Т1", "Т2", "Т1.11" };

    private static readonly Guid Case11NameGuid = new("a1c4e77b-2d3f-4c5a-8b9c-7d6e5f4a3b01");
    private static readonly Guid Case11ArticleGuid = new("a1c4e77b-2d3f-4c5a-8b9c-7d6e5f4a3b02");
    private static readonly Guid Case11MarkGuid = new("a1c4e77b-2d3f-4c5a-8b9c-7d6e5f4a3b03");
    private static readonly Guid Case11UnitGuid = new("a1c4e77b-2d3f-4c5a-8b9c-7d6e5f4a3b04");
    private static readonly Guid Case11CountGuid = new("a1c4e77b-2d3f-4c5a-8b9c-7d6e5f4a3b05");
    private static readonly Guid Case11SystemNameGuid = new("a1c4e77b-2d3f-4c5a-8b9c-7d6e5f4a3b06");

    // Ни одного Revit-типа в свойствах (правило 1 интеграционных тестов).
    private sealed class Case11SystemSeed
    {
        public string Name { get; init; } = string.Empty;

        public long ViewId { get; set; }

        public double RouteLengthFt { get; set; }

        public List<long> PipeIds { get; } = new();

        public List<long> FittingIds { get; } = new();
    }

    private sealed class Case11SheetSeed
    {
        public string Number { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public long Id { get; init; }
    }

    private sealed record Case11Definitions(
        ExternalDefinition Name,
        ExternalDefinition Article,
        ExternalDefinition Mark,
        ExternalDefinition Unit,
        ExternalDefinition Count,
        ExternalDefinition SystemName);

    private List<Case11SystemSeed> _case11Systems = null!;

    private List<Case11SheetSeed> _case11Album = null!;

    private SharedParameterFileScope? _case11SharedFile;

    private long _case11MainScheduleId;

    private int _case11ScheduleColumns;

    private string? _case11TitleBlockFamilyName;

    private string? _case11SeedError;

    [Test]
    public async Task Case11_Live()
    {
        if (!LiveConfigured())
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        try
        {
            SeedOnBenchBaseLive(11, Case11Title, SeedCase11);
            var run = RunProductionCase(
                11, Case11Title, Case11Text,
                Case11SeedSummary(), EvaluateCase11, DumpCase11State);

            // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
            var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
            await Assert.That(runHappened).IsTrue();
        }
        finally
        {
            _case11SharedFile?.Dispose();
        }
    }

    /// <summary>
    /// Механика экспликации без LLM: дословный повтор вызовов живого прогона
    /// 2026-11-22 (dry-run 3 items → real 3 items) на сидированном мире —
    /// репродукция «фильтров: 0» из вердикта Case11_Live.
    /// </summary>
    [Test]
    public async Task Case11_ExplicationMechanics()
    {
        try
        {
            SeedCase11();
            if (_case11SeedError is not null)
            {
                Skip.Test(_case11SeedError);
                return;
            }

            var context = new StubRevitContext(Doc);
            var tool = new BuildSystemSheetsTool(context, _transactions!, _changeSets!);

            var items = new List<string>();
            foreach (var system in _case11Systems)
            {
                items.Add(
                    "{\"viewId\":" + system.ViewId +
                    ",\"sheetName\":\"Аксонометрия " + system.Name + "\"," +
                    "\"explication\":{\"sourceScheduleId\":" + _case11MainScheduleId +
                    ",\"keepFields\":[\"Наименование\",\"Единица измерения\",\"Количество\"]," +
                    "\"filterField\":\"АДСК_ИмяСистемы\",\"filterValue\":\"" +
                    system.Name + "\"}}");
            }

            var payload = "\"items\":[" + string.Join(",", items) + "]";
            JsonElement ParseCase(string json)
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }

            var dry = tool.ExecuteForTests(
                ParseCase("{" + payload + ",\"dryRun\":true}"), approved: true, CancellationToken.None);
            await Assert.That(dry.Success).IsTrue();

            var real = tool.ExecuteForTests(
                ParseCase("{" + payload + ",\"dryRun\":false}"), approved: true, CancellationToken.None);
            await Assert.That(real.Success).IsTrue();

            foreach (var system in _case11Systems)
            {
                var sheet = Case11AllSheets()
                    .FirstOrDefault(candidate => candidate.Name == "Аксонометрия " + system.Name);
                await Assert.That(sheet).IsNotNull();
                var instance = Case11ScheduleOnSheet(sheet!);
                await Assert.That(instance).IsNotNull();
                var clone = sheet!.Document.GetElement(instance!.ScheduleId) as ViewSchedule;
                await Assert.That(clone).IsNotNull();

                var filters = clone!.Definition.GetFilters();
                await Assert.That(filters.Count).IsEqualTo(1);
                await Assert.That(filters[0].IsStringValue).IsTrue();
                await Assert.That(filters[0].GetStringValue()).IsEqualTo(system.Name);

                var filterFieldNames = string.Join("|", filters.Select(
                    f => clone.Definition.GetField(f.FieldId)?.GetName()));
                await Assert.That(filterFieldNames).IsEqualTo("АДСК_ИмяСистемы");
            }
        }
        finally
        {
            _case11SharedFile?.Dispose();
        }
    }

    /// <summary>Гейт сида для ночных прогонов без LLM: структура сида без live-гарда.</summary>
    [Test]
    public async Task Case11_SeedVerifies()
    {
        try
        {
            SeedOnBenchBase("case11", SeedCase11);

            await Assert.That(_case11Systems.Count).IsEqualTo(3);
            foreach (var system in _case11Systems)
            {
                await Assert.That(system.ViewId > 0).IsTrue();
                await Assert.That(system.PipeIds.Count).IsGreaterThanOrEqualTo(2);
                await Assert.That(system.FittingIds.Count).IsGreaterThanOrEqualTo(1);
            }

            // «Т1» — длинная трасса (20+ м), остальные — короткие.
            var lengths = _case11Systems.ToDictionary(
                system => system.Name, system => system.RouteLengthFt * 0.3048);
            await Assert.That(lengths["Т1"]).IsGreaterThanOrEqualTo(20.0);
            await Assert.That(lengths["Т2"] < lengths["Т1"]).IsTrue();
            await Assert.That(lengths["Т1.11"] < lengths["Т1"]).IsTrue();

            // «АДСК_ИмяСистемы» заполнен именем системы у ВСЕХ элементов (включая фитинги).
            foreach (var system in _case11Systems)
            {
                foreach (var elementId in system.PipeIds.Concat(system.FittingIds))
                {
                    var element = Doc.GetElement(ElementIdCompat.Create(elementId));
                    var value = element?.get_Parameter(Case11SystemNameGuid)?.AsString();
                    await Assert.That(value ?? string.Empty).IsEqualTo(system.Name);
                }
            }

            // Общая спецификация: 9-графка (6 общих + служебные) и строки по всем системам.
            var schedule = Doc.GetElement(ElementIdCompat.Create(_case11MainScheduleId)) as ViewSchedule;
            await Assert.That(schedule).IsNotNull();
            await Assert.That(_case11ScheduleColumns).IsGreaterThanOrEqualTo(8);
            await Assert.That(AgentScheduleReader.ReadRowCount(schedule!, skipHeader: true))
                .IsGreaterThanOrEqualTo(10);

            // Альбом 1..3 с кратностью 1 на каждом титульном блоке.
            await Assert.That(_case11Album.Count).IsEqualTo(3);
            foreach (var album in _case11Album)
            {
                var sheet = Doc.GetElement(ElementIdCompat.Create(album.Id)) as ViewSheet;
                await Assert.That(sheet).IsNotNull();
                await Assert.That(sheet!.SheetNumber).IsEqualTo(album.Number);
                await Assert.That(sheet.Name).IsEqualTo(album.Name);
                await Assert.That(Case11MultiplicityOf(sheet)).IsEqualTo(1);
            }
        }
        finally
        {
            _case11SharedFile?.Dispose();
        }
    }

    // ── Сид (титульный блок, системы, документация, общая спецификация) —
    // в Case11.Seed.cs; определения ФОП, трассы, заполнение элементов и
    // верификация сида, сводка/дамп/тулы — в Case11.State.cs; вердикт и
    // геометрические проверки — в Case11.Evaluation.cs.
}
