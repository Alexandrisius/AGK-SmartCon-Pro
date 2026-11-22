using System.Text;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Interfaces.Agent;
using Autodesk.Revit.DB;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 12 of the real-cases benchmark = v2 case 7 (write, model + schedule):
/// the engineering spec is sorted "по алфавиту имён семейств"; the model must
/// introduce a TYPE-level code parameter («10. Оборудование основное» …),
/// give every type of a family the same code, re-sort the main schedule by it
/// and leave a working table («Рабочая») with family/type/name columns.
/// ОТСОГЛАСОВАННОЕ УПРОЩЕНИЕ СИДА: все ~16 позиций владельца сведены к
/// generic-семействам ОДНОЙ категории OST_GenericModel — спецификация Revit
/// строится на одну категорию, а кейс проверяет СОРТИРОВКУ и КОДЫ (механику
/// владельца «рабочая таблица семейство/типоразмер/наименование»), не
/// категории. Эталонный порядок групп: 10 Оборудование → 20 Арматура →
/// 30 Трубопроводы → 40 Фитинги → 50 Материалы. Вердикт — в партиале
/// Case12.Evaluation (EvaluateCase12). Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/real-cases-benchmark-v2.md, кейс 7).
    private const string Case12Text =
        "Спецификация инженерных систем сейчас отсортирована бессмысленно — по " +
        "алфавиту имён семейств. Наведи в ней инженерный порядок, как принято в " +
        "рабочих спецификациях: от главного оборудования к мелочи и материалам. " +
        "Сделай это через параметр типоразмера с кодами вида «10. Оборудование " +
        "основное», «20. Арматура» — код + понятное название группы, и отсортируй " +
        "по нему спецификацию. Чтобы разобраться, что есть что, собери себе рабочую " +
        "таблицу с полями «семейство / типоразмер / наименование» и раскладывай по " +
        "ней; рабочую таблицу оставь в проекте с пометкой \"Рабочая\". Код единый " +
        "на всё семейство: у всех типоразмеров одного семейства одна группа.";

    private const string Case12Title = "Инженерная сортировка спецификации через параметр-код";

    private const string Case12ScheduleName = "Спецификация инженерных систем";

    private static readonly int[] Case12ReferenceOrder = { 10, 20, 30, 40, 50 };

    // ~16 позиций владельца → 10 generic-семейств / 14 типоразмеров / 5 групп
    // (упрощение категорий зафиксировано в шапке класса). Rank = эталонная
    // значимость группы (10 оборудование … 50 материалы).
    private static (string FamilyName, string[] TypeNames, int Rank)[] Case12FamilySpec() => new[]
    {
        ("Вентустановка В-1", new[] { "В-1.1", "В-1.2" }, 10),
        ("Кондиционер К-2", new[] { "К-2.1" }, 10),
        ("Вентилятор ВН-3", new[] { "ВН-3.1" }, 10),
        ("Кран шаровой", new[] { "Ду15", "Ду25" }, 20),
        ("Труба", new[] { "Сталь", "Медь", "ПВХ" }, 30),
        ("Отвод", new[] { "90 градусов" }, 40),
        ("Тройник", new[] { "равнопроходной" }, 40),
        ("Переход", new[] { "концентрический" }, 40),
        ("Грунтовка ГФ-021", new[] { "ведро 25 кг" }, 50),
        ("Краска ПФ-115", new[] { "банка 10 кг" }, 50)
    };

    private List<CaseGenericFamilySeed> _case12Families = null!;

    private Dictionary<string, int> _case12Ranks = null!;

    private long _case12ScheduleId;

    private string _case12SortFieldName = string.Empty;

    private string? _case12SeedError;

    [Test]
    public async Task Case12_Live()
    {
        var config = DogfoodConfig.TryLoad();
        if (config is null)
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        try
        {
            SeedCase12();
            var run = RunCase(
                12, Case12Title, Case12Text, config, CreateCase12Tools(),
                Case12SeedSummary(), EvaluateCase12, DumpCase12State);

            // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
            var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
            await Assert.That(runHappened).IsTrue();
        }
        finally
        {
            DisposeCaseGenericFamilies(_case12Families);
        }
    }

    [Test]
    public async Task Case12_SeedVerifies()
    {
        try
        {
            SeedCase12();
            var typeCount = _case12Families.Sum(family => family.Types.Count);
            await Assert.That(_case12Families.Count).IsEqualTo(10);
            await Assert.That(typeCount).IsEqualTo(14);
            await Assert.That(_case12Ranks.Values.Distinct().Count()).IsEqualTo(5);
            await Assert.That(_case12Families.Sum(family =>
                    family.Types.Count(type => type.InstanceId != 0)))
                .IsEqualTo(14);

            var schedule = Case12MainSchedule();
            await Assert.That(schedule).IsNotNull();
            await Assert.That(AgentScheduleReader.ReadColumns(Doc, schedule!, out _).Count)
                .IsGreaterThanOrEqualTo(4);
            await Assert.That(AgentScheduleReader.ReadRowCount(schedule!, skipHeader: true))
                .IsGreaterThanOrEqualTo(13);

            // Параметра-кода в проекте НЕТ до прогона.
            await Assert.That(DetectCase12CodeParameter(out _, out _)).IsNull();

            // Исходный порядок строк — НЕ эталонный («плохая» алфавитная сортировка).
            var rows = Case12RowRanks(out var mapped, out _);
            await Assert.That(rows.SequenceEqual(Case12ReferenceOrder)).IsFalse();
            await Assert.That(mapped).IsGreaterThanOrEqualTo(13);
        }
        finally
        {
            DisposeCaseGenericFamilies(_case12Families);
        }
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase12()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _case12SeedError = null;
        var spec = Case12FamilySpec();
        _case12Ranks = spec.ToDictionary(item => item.FamilyName, item => item.Rank);

        // Семейства грузятся ВНЕ транзакции (LoadFamily на изменяемом документе падает).
        var families = SeedCaseGenericFamilies(
            spec.Select(item => (item.FamilyName, item.TypeNames)).ToArray(),
            out var skipReason);
        if (families is null)
        {
            Skip.Test(skipReason ?? "Сид-семейства кейса 12 не собрались");
            return;
        }

        _case12Families = families;
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed case 12 (sorting)", doc =>
        {
            PlaceCaseGenericInstances(doc, _case12Families);
            SeedCase12Schedule(doc);
        });

        if (_case12SeedError is not null)
        {
            Skip.Test(_case12SeedError);
            return;
        }

        VerifyCase12Seed();
    }

    private void SeedCase12Schedule(Document doc)
    {
        ViewSchedule schedule;
        try
        {
            schedule = ViewSchedule.CreateSchedule(
                doc, new ElementId(BuiltInCategory.OST_GenericModel));
        }
        catch (Exception ex)
        {
            _case12SeedError = "Не удалось создать спецификацию кейса 12: " +
                               ex.GetType().Name + ": " + ex.Message;
            return;
        }

        schedule.Name = Case12ScheduleName;
        var definition = schedule.Definition;
        var fieldNames = new List<string>();

        void TryAdd(SchedulableField? field)
        {
            if (field is null)
                return;

            definition.AddField(field);
            fieldNames.Add(field.GetName(doc));
        }

        TryAdd(FindCaseSchedulableFieldByName(doc, definition, "Семейство", "Family"));
        TryAdd(FindCaseSchedulableFieldByName(
            doc, definition, "Тип", "Type", "Типоразмер", "Type Name"));
        foreach (var candidate in new[]
                 {
                     BuiltInParameter.ALL_MODEL_TYPE_MARK,
                     BuiltInParameter.ALL_MODEL_MARK,
                     BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS
                 })
        {
            if (fieldNames.Count >= 4)
                break;

            foreach (var field in definition.GetSchedulableFields())
            {
                if (field.ParameterId == new ElementId(candidate))
                {
                    TryAdd(field);
                    break;
                }
            }
        }

        if (fieldNames.Count < 4)
        {
            _case12SeedError = "Спецификация кейса 12 собрала только " + fieldNames.Count +
                               " поля (нужно ≥4): " + string.Join(", ", fieldNames);
            return;
        }

        // «Плохая» сортировка: алфавит первой колонки (семейство, иначе тип),
        // запасной вариант — первое сортируемое поле.
        ScheduleFieldId? sortFieldId = null;
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var fieldName = definition.GetField(fieldId)?.GetName();
            if (string.Equals(fieldName, fieldNames[0], StringComparison.OrdinalIgnoreCase) &&
                CanCase12Sort(definition, fieldId))
            {
                sortFieldId = fieldId;
                break;
            }
        }

        sortFieldId ??= definition.GetFieldOrder()
            .FirstOrDefault(fieldId => CanCase12Sort(definition, fieldId));
        if (sortFieldId is null)
        {
            _case12SeedError = "Ни одно поле спецификации кейса 12 не сортируется";
            return;
        }

        _case12SortFieldName = definition.GetField(sortFieldId).GetName();
        definition.AddSortGroupField(
            new ScheduleSortGroupField(sortFieldId, ScheduleSortOrder.Ascending));
        _case12ScheduleId = schedule.Id.GetValue();
    }

    private static bool CanCase12Sort(ScheduleDefinition definition, ScheduleFieldId fieldId)
    {
        try
        {
            return definition.CanSortByField(fieldId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void VerifyCase12Seed()
    {
        var schedule = Case12MainSchedule();
        if (schedule is null || schedule.Id.GetValue() != _case12ScheduleId)
        {
            Skip.Test("Основная спецификация кейса 12 не найдена после сида");
            return;
        }

        // Имена семейств обязаны совпадать со спецификацией сида (ранее temp-префикс
        // .rfa утекал в имя семейства и сваливал проверку групп — живой прогон 2026-11-22).
        var unmatched = _case12Families
            .Where(family => !_case12Ranks.ContainsKey(family.FamilyName))
            .Select(family => family.FamilyName)
            .ToList();
        if (unmatched.Count > 0)
        {
            Skip.Test("Сид кейса 12: семейства вне карты групп (имена не совпали со спецификацией): " +
                      string.Join(", ", unmatched));
            return;
        }

        var groups = _case12Families
            .Select(family => _case12Ranks[family.FamilyName])
            .Distinct()
            .ToList();
        if (groups.Count < 5)
        {
            Skip.Test("Сид кейса 12 некорректен: групп семейств меньше 5 (" + groups.Count + ")");
            return;
        }

        var typeCount = _case12Families.Sum(family => family.Types.Count);
        if (typeCount < 14)
        {
            Skip.Test("Сид кейса 12 некорректен: типоразмеров " + typeCount + " (нужно ≥14)");
            return;
        }

        if (AgentScheduleReader.ReadRowCount(schedule, skipHeader: true) < 13)
        {
            Skip.Test("Сид кейса 12 некорректен: строк в основной спецификации " +
                      AgentScheduleReader.ReadRowCount(schedule, skipHeader: true));
        }
    }

    private List<string> Case12SeedSummary()
    {
        var lines = new List<string>
        {
            "10 generic-семейств / 14 типоразмеров / по одному размещённому экземпляру типа",
            "(упрощение сида, согласовано с механикой владельца: все позиции — Generic Model " +
            "одной категории; кейс проверяет сортировку и коды, не категории)",
            "эталонный порядок групп: 10 Оборудование (Вентустановка В-1 ×2 типа, Кондиционер, " +
            "Вентилятор) → 20 Арматура (Кран шаровой ×2) → 30 Трубопроводы (Труба: Сталь/Медь/ПВХ) " +
            "→ 40 Фитинги (Отвод, Тройник, Переход) → 50 Материалы (Грунтовка, Краска)",
            "основная спека «" + Case12ScheduleName + "» (Generic Model), сортировка по полю «" +
            _case12SortFieldName + "» — алфавит, порядок бессмысленный",
            "параметра сортировки/кода в проекте НЕТ — путь выбирает агент"
        };
        foreach (var family in _case12Families)
        {
            var rank = _case12Ranks.TryGetValue(family.FamilyName, out var value) ? value : 0;
            lines.Add("  - «" + family.FamilyName + "» → группа " + rank + ": " +
                      string.Join(", ", family.Types.Select(type => type.TypeName)));
        }

        return lines;
    }

    private string DumpCase12State()
    {
        var builder = new StringBuilder();
        var codes = DetectCase12CodeParameter(out var familyCodes, out var uncoded);
        builder.AppendLine("| Семейство | Группа | Код (сейчас) |");
        builder.AppendLine("|---|---|---|");
        foreach (var family in _case12Families)
        {
            var rank = _case12Ranks.TryGetValue(family.FamilyName, out var value) ? value : 0;
            var code = familyCodes.TryGetValue(family.FamilyName, out var familyCode)
                ? familyCode
                : string.Empty;
            builder.AppendLine("| " + family.FamilyName + " | " + rank + " | " + code + " |");
        }

        if (codes is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Кодовый параметр: «" + codes + "»; без кода: " +
                               (uncoded.Count == 0 ? "нет" : string.Join(", ", uncoded)));
        }

        builder.AppendLine();
        builder.AppendLine("Порядок групп по строкам основной спеки: " +
                           string.Join(" → ", Case12RowRanks(out _, out _)));
        return builder.ToString();
    }

    // ── Тулы ────────────────────────────────────────────────────────────

    private List<IAgentTool> CreateCase12Tools()
    {
        var context = new StubRevitContext(Doc);
        var tools = new List<IAgentTool>
        {
            new ListSchedulesTool(context),
            new GetScheduleDefinitionTool(context),
            new GetScheduleDataTool(context),
            new ListTypeParameterCandidatesTool(context),
            new ListFamiliesTool(context),
            new ListFamilyTypesTool(context),
            new GetProjectOverviewTool(context),
            new ListCategoriesTool(context),
            new CreateScheduleTool(context, _transactions!, _changeSets!),
            new AddScheduleFieldTool(context, _transactions!, _changeSets!),
            new SetScheduleFieldOrderTool(context, _transactions!, _changeSets!),
            new SetScheduleSortTool(context, _transactions!, _changeSets!),
            new SetScheduleFieldVisibilityTool(context, _transactions!, _changeSets!),
            new SetParametersTool(context, _transactions!, _changeSets!),
            new UndoLastTool(context, _transactions!, _changeSets!)
        };

        AddCaseFindTools(tools);
        return tools;
    }

    // ── Общие локаторы основной спеки ───────────────────────────────────

    /// <summary>Основная спека: по имени, иначе — любая generic-спека с ≥8 строками.</summary>
    private ViewSchedule? Case12MainSchedule()
    {
        foreach (var schedule in AgentScheduleReader.Collect(Doc))
        {
            if (string.Equals(schedule.Name, Case12ScheduleName, StringComparison.OrdinalIgnoreCase))
                return schedule;
        }

        return AgentScheduleReader.Collect(Doc).FirstOrDefault(Case12LooksLikeMainSchedule);
    }

    private bool Case12LooksLikeMainSchedule(ViewSchedule schedule)
    {
        try
        {
            return schedule.Definition.CategoryId ==
                   new ElementId(BuiltInCategory.OST_GenericModel) &&
                   AgentScheduleReader.ReadRowCount(schedule, skipHeader: true) >= 8;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
