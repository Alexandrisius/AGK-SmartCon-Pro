using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

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
/// считает, вердикт не наблюдает). Вердикт — в партиале
/// Case11.Evaluation (EvaluateCase11). Partial of
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
        var config = DogfoodConfig.TryLoad();
        if (config is null)
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        try
        {
            SeedCase11();
            var run = RunCase(
                11, Case11Title, Case11Text, config, CreateCase11Tools(),
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

    /// <summary>Гейт сида для ночных прогонов без LLM: структура сида без live-гарда.</summary>
    [Test]
    public async Task Case11_SeedVerifies()
    {
        try
        {
            SeedCase11();

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

    // ── Сид ─────────────────────────────────────────────────────────────
    // Определения ФОП, трассы, заполнение элементов и верификация сида —
    // в Case11.State.cs; сводка/дамп/тулы — там же; вердикт — в
    // Case11.Evaluation.cs.

    private void SeedCase11()
    {
        // MEP-шаблон установки: нужны PipeType/PipingSystemType (паттерн кейса 10/15).
        _document = SampleFiles.NewMepTemplateDocument(Application);
        _document ??= Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasPipeTypes(Doc))
        {
            Skip.Test("В шаблоне проекта нет типов труб — сид кейса 11 невозможен");
            return;
        }

        _case11SeedError = null;
        _case11Systems = new List<Case11SystemSeed>();
        _case11Album = new List<Case11SheetSeed>();
        _case11SharedFile = SharedParameterFileScope.Create(Application);
        var definitions = SeedCase11Definitions();
        if (_case11SeedError is not null)
        {
            Skip.Test(_case11SeedError);
            return;
        }

        // Загрузка титблока + EditFamily-параметр кратности — ВНЕ транзакций
        // (LoadFamily/LoadFamily(back) на изменяемом документе падает).
        if (!PrepareCase11TitleBlock())
        {
            Skip.Test(_case11SeedError ?? "Титульный блок кейса 11 не подготовлен");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        // Транзакция 1: системы (трубы + фитинги + привязка общих параметров).
        _transactions.RunInTransaction(Doc, "Seed case 11 (systems)", doc =>
        {
            try
            {
                SeedCase11Systems(doc, definitions!);
            }
            catch (Exception ex)
            {
                _case11SeedError = "Сид систем кейса 11: " + ex.GetType().Name + ": " + ex.Message;
            }
        });

        if (_case11SeedError is not null)
        {
            Skip.Test(_case11SeedError);
            return;
        }

        // Транзакция 2: значения параметров (материализуются после коммита привязки —
        // механика кейса 13), 3D-виды, общая спецификация, альбом.
        _transactions.RunInTransaction(Doc, "Seed case 11 (documentation)", doc =>
        {
            try
            {
                SeedCase11Documentation(doc, definitions!);
            }
            catch (Exception ex)
            {
                _case11SeedError = "Сид документации кейса 11: " + ex.GetType().Name + ": " + ex.Message;
            }
        });

        if (_case11SeedError is not null)
        {
            Skip.Test(_case11SeedError);
            return;
        }

        VerifyCase11Seed();
    }

    /// <summary>
    /// Титульный блок с редактируемым параметром кратности: загрузка .rfa из
    /// библиотеки установки → EditFamily → FamilyManager.AddParameter
    /// («Кратность размера», Integer, instance) → LoadFamily обратно.
    /// </summary>
    private bool PrepareCase11TitleBlock()
    {
        var titleBlockId = ModelSeed.FindTitleBlockTypeId(Doc);
        if (titleBlockId is null)
        {
            var path = ModelSeed.FindTitleBlockFamilyFile(Doc.Application.VersionNumber);
            if (path is null)
            {
                _case11SeedError = "В библиотеке установки нет .rfa титульного блока — сид кейса 11 невозможен";
                return false;
            }

            try
            {
                if (!Doc.LoadFamily(path, out _))
                {
                    _case11SeedError = "Не удалось загрузить титульный блок " + path;
                    return false;
                }
            }
            catch (Exception ex)
            {
                _case11SeedError = "Загрузка титульного блока: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            titleBlockId = ModelSeed.FindTitleBlockTypeId(Doc);
        }

        if (Doc.GetElement(titleBlockId ?? ElementId.InvalidElementId) is not FamilySymbol symbol ||
            symbol.Family is not { } family)
        {
            _case11SeedError = "Тип титульного блока не читается после загрузки";
            return false;
        }

        _case11TitleBlockFamilyName = family.Name;

        Document? familyDoc = null;
        try
        {
            familyDoc = Doc.EditFamily(family);
            using (var transaction = new Transaction(familyDoc, "Add size multiplicity"))
            {
                transaction.Start();
                familyDoc.FamilyManager.AddParameter(
                    Case11TitleblockMultiplicity, GroupTypeId.Data, SpecTypeId.Int.Integer,
                    isInstance: true);
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    _case11SeedError = "Транзакция параметра кратности титульного семейства не завершилась";
                    return false;
                }
            }

            var loaded = familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
            if (loaded is null)
            {
                _case11SeedError = "Титульное семейство с кратностью не загрузилось обратно в проект";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _case11SeedError = "EditFamily титульного блока кейса 11: " +
                               ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            CloseCaseFamilyDoc(familyDoc);
        }
    }

    /// <summary>
    /// Три системы, каждая — на СВОЁМ дубликате PipingSystemType (разные типы
    /// гарантируют разные MEPSystem), трубы + колена-фитинги на изломах
    /// (Case15-механика), переименование логической системы в имя кейса.
    /// </summary>
    private void SeedCase11Systems(Document doc, Case11Definitions definitions)
    {
        var level = ModelSeed.CreateLevel(doc, 0.0);
        level.Name = "Этаж 1";

        var pipeTypeId = BenchFirstTypeId(doc, typeof(PipeType));
        var baseSystemType = doc.GetElement(BenchFirstTypeId(doc, typeof(PipingSystemType))) as ElementType;
        if (pipeTypeId is null || baseSystemType is null)
        {
            _case11SeedError = "PipeType/PipingSystemType исчезли после проверки шаблона";
            return;
        }

        // Привязка общих параметров к трубам и фитингам (instance binding) —
        // до записи значений, значения в следующей транзакции (механика кейса 13).
        var app = doc.Application;
        var categorySet = app.Create.NewCategorySet();
        categorySet.Insert(Category.GetCategory(doc, BuiltInCategory.OST_PipeCurves));
        categorySet.Insert(Category.GetCategory(doc, BuiltInCategory.OST_PipeFitting));
        var binding = app.Create.NewInstanceBinding(categorySet);
        foreach (var definition in new[]
                 {
                     definitions.Name, definitions.Article, definitions.Mark,
                     definitions.Unit, definitions.Count, definitions.SystemName
                 })
        {
            if (!doc.ParameterBindings.Insert(definition, binding))
            {
                _case11SeedError = "Не удалось привязать общий параметр «" +
                                   definition.Name + "» к трубам/фитингам";
                return;
            }
        }

        var routes = Case11Routes();
        for (var index = 0; index < Case11Systems.Length; index++)
        {
            var name = Case11Systems[index];
            var points = routes[index];
            var seed = new Case11SystemSeed { Name = name };
            var systemTypeId = baseSystemType.Duplicate("SC_Кейс11_" + name.Replace('.', '_')).Id;

            Pipe? previous = null;
            for (var segment = 1; segment < points.Length; segment++)
            {
                var pipe = Pipe.Create(
                    doc, systemTypeId, pipeTypeId, level.Id, points[segment - 1], points[segment]);
                seed.PipeIds.Add(pipe.Id.GetValue());
                seed.RouteLengthFt += points[segment - 1].DistanceTo(points[segment]);
                doc.Regenerate();

                if (previous is not null)
                {
                    var joint = points[segment - 1];
                    var elbow = doc.Create.NewElbowFitting(
                        ModelSeed.FindConnectorAt(previous, joint),
                        ModelSeed.FindConnectorAt(pipe, joint));
                    seed.FittingIds.Add(elbow.Id.GetValue());
                    doc.Regenerate();
                }

                previous = pipe;
            }

            Case15RenameElement(doc.GetElement(previous!.MEPSystem?.Id ?? ElementId.InvalidElementId), name);
            doc.Regenerate();
            _case11Systems.Add(seed);
        }
    }

    private void SeedCase11Documentation(Document doc, Case11Definitions definitions)
    {
        // 1. Значения общих параметров всем элементам систем (наследие кейса 4:
        // «АДСК_ИмяСистемы» заполнен всем, включая фитинги).
        foreach (var system in _case11Systems)
        {
            foreach (var pipeId in system.PipeIds)
                Case11FillElement(doc, definitions, pipeId, system.Name, isFitting: false);
            foreach (var fittingId in system.FittingIds)
                Case11FillElement(doc, definitions, fittingId, system.Name, isFitting: true);
        }

        // 2. 3D-вид на каждую систему (фильтр не нужен: кейс считает его сделанным).
        var viewType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional);
        if (viewType is null)
        {
            _case11SeedError = "В шаблоне проекта нет ViewFamilyType для 3D-вида";
            return;
        }

        foreach (var system in _case11Systems)
        {
            var view = View3D.CreateIsometric(doc, viewType.Id);
            view.Name = system.Name;
            system.ViewId = view.Id.GetValue();
        }

        // 3. Общая спецификация — мультикатегорийная (трубы И фитинги в одной
        // 9-графке): 6 общих полей + до 3 служебных built-in.
        SeedCase11MainSchedule(doc);

        // 4. Альбом 1..3 с кратностью 1.
        var titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
        if (titleBlockId is null)
        {
            _case11SeedError = "Тип титульного блока исчез после EditFamily-цикла";
            return;
        }

        var albumNames = new[] { "Общие данные", "План 1 этажа", "План 2 этажа" };
        for (var index = 0; index < albumNames.Length; index++)
        {
            var sheet = ViewSheet.Create(doc, titleBlockId);
            sheet.SheetNumber = (index + 1).ToString(CultureInfo.InvariantCulture);
            sheet.Name = albumNames[index];
            _case11Album.Add(new Case11SheetSeed
            {
                Number = (index + 1).ToString(CultureInfo.InvariantCulture),
                Name = albumNames[index],
                Id = sheet.Id.GetValue()
            });
        }

        doc.Regenerate();
        foreach (var album in _case11Album)
        {
            if (doc.GetElement(ElementIdCompat.Create(album.Id)) is not ViewSheet sheet)
                continue;

            Case11TitleBlockOf(sheet)?.LookupParameter(Case11TitleblockMultiplicity)?.Set(1);
        }
    }

    private void SeedCase11MainSchedule(Document doc)
    {
        ViewSchedule schedule;
        try
        {
            schedule = ViewSchedule.CreateSchedule(doc, ElementId.InvalidElementId);
        }
        catch (Exception ex)
        {
            _case11SeedError = "Мультикатегорийная спецификация не создалась: " +
                               ex.GetType().Name + ": " + ex.Message;
            return;
        }

        schedule.Name = Case11MainScheduleName;
        var definition = schedule.Definition;
        foreach (var guid in new[]
                 {
                     Case11NameGuid, Case11ArticleGuid, Case11MarkGuid,
                     Case11UnitGuid, Case11CountGuid, Case11SystemNameGuid
                 })
        {
            if (!TryAddCaseSharedField(doc, definition, guid))
            {
                _case11SeedError = "Общее поле (guid …" +
                                   guid.ToString("D").Substring(guid.ToString("D").Length - 6) +
                                   ") не найдено среди schedulable мультикатегорийной спеки";
                return;
            }
        }

        // Служебные built-in: до трёх из кандидатов (состав зависит от локализации).
        var added = 0;
        foreach (var candidates in new[]
                 {
                     new[] { "Семейство", "Family" },
                     new[] { "Тип", "Type Name", "Типоразмер" },
                     new[] { "Комментарий", "Comments" }
                 })
        {
            if (added >= 3)
                break;

            var field = FindCaseSchedulableFieldByName(doc, definition, candidates);
            if (field is not null)
            {
                definition.AddField(field);
                added++;
            }
        }

        _case11ScheduleColumns = AgentScheduleReader.ReadColumns(Doc, schedule, out _).Count;
        _case11MainScheduleId = schedule.Id.GetValue();
    }

    // ── Сводка сида, дамп состояния и тулы — в Case11.State.cs ──────────
    // Вердикт и геометрические проверки — в Case11.Evaluation.cs.
}
