using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 13 of the real-cases benchmark = v2 case 8 (audit + safe write): the
/// mandatory parameter set is derived from the fields of the schedule PLACED
/// ON A SHEET; the draft schedule outside sheets (field «Комментарий») is a
/// bait and must not be audited. The batch summary (filled/empty per
/// parameter + culprits family/type) must match the seed truth; the only
/// self-fill allowed is the deterministic one (fittings unit = «шт»);
/// meaningful holes (Наименование) must stay empty; etalons untouched.
/// ОТСОГЛАСОВАННОЕ УПРОЩЕНИЕ СИДА: все ~12 позиций — generic-семейства одной
/// категории, значения общих параметров живут на ТИПАХ (type binding), как в
/// реальности у инстансов общие атрибуты наследуются от типоразмера. Вердикт —
/// в партиале Case13.Evaluation (EvaluateCase13).
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/real-cases-benchmark-v2.md, кейс 8).
    private const string Case13Text =
        "Проведи аудит модели перед выдачей заказчику. Критичная отчётность — " +
        "спецификации, вынесенные на листы: по их полям определи, какие общие " +
        "параметры обязаны быть заполнены. Быстро проверь заполненность этих " +
        "параметров по всей модели и дай экспресс-сводку: по каждому параметру — " +
        "сколько пустых из скольких, и список виновников (семейство и типоразмер), " +
        "чтобы я сразу видел, где чинить. Что заполняется однозначно (например " +
        "единицы измерения по характеру элемента: трубы — в метрах, оборудование — " +
        "в штуках) — заполни сразу и покажи, что заполнил и почему; остальное " +
        "не трогай, только подсвети.";

    private const string Case13Title = "Аудит заполненности перед выдачей заказчику";

    private const string Case13ScheduleName = "Ведомость оборудования";
    private const string Case13BaitScheduleName = "Прикидка объёмов (черновик)";
    private const string Case13SheetNumber = "SC-C13-01";
    private const string Case13NameParameter = "Наименование";
    private const string Case13MarkParameter = "Марка";
    private const string Case13UnitParameter = "Единица измерения";
    private const string Case13CountParameter = "Количество";
    private const string Case13BaitParameter = "Комментарий";
    private const string Case13SharedGroup = "СК_Кейс13";
    private const int Case13ItemCount = 12;

    private static readonly Guid Case13NameGuid = new("3f7a1c9e-8b2d-4e5f-9a6b-1c2d3e4f5a01");
    private static readonly Guid Case13MarkGuid = new("3f7a1c9e-8b2d-4e5f-9a6b-1c2d3e4f5a02");
    private static readonly Guid Case13UnitGuid = new("3f7a1c9e-8b2d-4e5f-9a6b-1c2d3e4f5a03");
    private static readonly Guid Case13CountGuid = new("3f7a1c9e-8b2d-4e5f-9a6b-1c2d3e4f5a04");
    private static readonly Guid Case13BaitGuid = new("3f7a1c9e-8b2d-4e5f-9a6b-1c2d3e4f5a05");

    // Истина сида: 12 позиций; null = дырка (Name/Mark — содержательные,
    // Unit — безопасная, заполняется однозначно). Значения на ТИПАХ.
    internal static (string Family, string Type, string? Name, string? Mark, string? Unit)[]
        Case13Truth() => new[]
        {
            ("Вентустановка В-1", "В-1", (string?)null, "В-1", "шт"),
            ("Кондиционер К-2", "К-2", (string?)null, "К-2", "шт"),
            ("Насос Н-3", "Н-3", "Насос центробежный", "Н-3", "шт"),
            ("Отвод 90", "О-1", "Отвод 90 градусов", "О-1", (string?)null),
            ("Тройник", "Т-1", "Тройник равнопроходной", "Т-1", (string?)null),
            ("Переход", "П-1", "Переход концентрический", "П-1", (string?)null),
            ("Крестовина", "КР-1", "Крестовина", "КР-1", (string?)null),
            ("Труба", "Сталь", "Труба стальная", "ТС-1", "м"),
            ("Труба", "Медь", "Труба медная", (string?)null, "м"),
            ("Труба", "ПВХ", "Труба ПВХ", "ТП-1", "м"),
            ("Задвижка З-1", "З-1", "Задвижка", "З-1", "шт"),
            ("Фильтр Ф-2", "Ф-2", "Фильтр сетчатый", "Ф-2", "шт")
        };

    private static Dictionary<string, string> Case13Kinds() => new(StringComparer.Ordinal)
    {
        ["Вентустановка В-1"] = "equipment",
        ["Кондиционер К-2"] = "equipment",
        ["Насос Н-3"] = "equipment",
        ["Отвод 90"] = "fitting",
        ["Тройник"] = "fitting",
        ["Переход"] = "fitting",
        ["Крестовина"] = "fitting",
        ["Труба"] = "pipe",
        ["Задвижка З-1"] = "etalon",
        ["Фильтр Ф-2"] = "etalon"
    };

    // Ни одного Revit-типа в свойствах (rule 1 интеграционных тестов).
    private sealed class Case13ItemSeed
    {
        public string Family { get; init; } = string.Empty;

        public string TypeName { get; init; } = string.Empty;

        public long TypeId { get; init; }

        public long InstanceId { get; init; }

        public string? Name { get; init; }

        public string? Mark { get; init; }

        public string? Unit { get; init; }

        public string Kind { get; init; } = string.Empty;
    }

    // Создаётся только внутри сида (rule 1: без статической инициализации).
    private sealed record Case13Definitions(
        ExternalDefinition Name,
        ExternalDefinition Mark,
        ExternalDefinition Unit,
        ExternalDefinition Count,
        ExternalDefinition Bait);

    private List<Case13ItemSeed> _case13Items = null!;

    private List<CaseGenericFamilySeed> _case13Families = null!;

    private SharedParameterFileScope? _case13SharedFile;

    private long _case13ScheduleId;

    private long _case13BaitScheduleId;

    private bool _case13SheetCreated;

    private string? _case13SeedError;

    [Test]
    public async Task Case13_Live()
    {
        if (!LiveConfigured())
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        try
        {
            SeedOnBenchBaseLive(13, Case13Title, SeedCase13);
            var run = RunProductionCase(
                13, Case13Title, Case13Text,
                Case13SeedSummary(), EvaluateCase13, DumpCase13State);

            // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
            var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
            await Assert.That(runHappened).IsTrue();
        }
        finally
        {
            DisposeCaseGenericFamilies(_case13Families);
            _case13SharedFile?.Dispose();
        }
    }

    [Test]
    public async Task Case13_SeedVerifies()
    {
        try
        {
            SeedOnBenchBase("case13", SeedCase13);
            await Assert.That(_case13Items.Count).IsEqualTo(Case13ItemCount);
            await Assert.That(_case13Items.Count(item => item.InstanceId != 0))
                .IsEqualTo(Case13ItemCount);

            // Дырки на месте: Name 2, Mark 1, Unit 4, Count заполнен у всех.
            await Assert.That(_case13Items.Count(item => Case13IsEmpty(item, Case13NameGuid)))
                .IsEqualTo(2);
            await Assert.That(_case13Items.Count(item => Case13IsEmpty(item, Case13MarkGuid)))
                .IsEqualTo(1);
            await Assert.That(_case13Items.Count(item => Case13IsEmpty(item, Case13UnitGuid)))
                .IsEqualTo(4);
            await Assert.That(_case13Items.Count(item => Case13IsEmpty(item, Case13CountGuid)))
                .IsEqualTo(0);

            // Спека на листе, приманка вне листов и несёт поле «Комментарий».
            var sheetNumbers = AgentScheduleReader.MapSheetNumbers(Doc);
            await Assert.That(sheetNumbers.TryGetValue(_case13ScheduleId, out var numbers) &&
                              numbers!.Count > 0).IsTrue();
            await Assert.That(sheetNumbers.TryGetValue(_case13BaitScheduleId, out _)).IsFalse();
            var bait = Doc.GetElement(ElementIdCompat.Create(_case13BaitScheduleId)) as ViewSchedule;
            await Assert.That(bait).IsNotNull();
            await Assert.That(AgentScheduleReader.ReadColumns(Doc, bait!, out _).Any(column =>
                    ContainsText(column.Heading, Case13BaitParameter) ||
                    ContainsText(column.ParameterName ?? string.Empty, Case13BaitParameter)))
                .IsTrue();
        }
        finally
        {
            DisposeCaseGenericFamilies(_case13Families);
            _case13SharedFile?.Dispose();
        }
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase13()
    {
        _document = BenchDocuments.NewBase(Application);
        _case13SeedError = null;
        _case13Items = new List<Case13ItemSeed>();
        _case13SharedFile = SharedParameterFileScope.Create(Application);

        var truth = Case13Truth();
        var kinds = Case13Kinds();
        var spec = truth
            .GroupBy(item => item.Family)
            .Select(group => (group.Key, group.Select(item => item.Type).ToArray()))
            .ToArray();
        var families = SeedCaseGenericFamilies(spec, out var skipReason);
        if (families is null)
        {
            Skip.Test(skipReason ?? "Сид-семейства кейса 13 не собрались");
            return;
        }

        // Определения ФОП создаются ПОСЛЕ загрузки семейств: перезапись тёзок
        // шаблона владельца заставляет Revit перечитывать ФОП, и обёртки,
        // полученные раньше, протухают (InvalidObjectExceptionProxy в Insert).
        var definitions = SeedCase13Definitions();
        if (_case13SeedError is not null)
        {
            Skip.Test(_case13SeedError);
            return;
        }

        _case13Families = families;
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        // Биндинг и запись значений — в РАЗНЫХ транзакциях: параметры type binding
        // материализуются на элементах только при коммите, get_Parameter(GUID) внутри
        // той же транзакции вернёт null и значения молча потеряются.
        _transactions.RunInTransaction(Doc, "Seed case 13 (bind)", doc =>
        {
            definitions = BindCase13Parameters(doc, definitions!);
            if (_case13SeedError is not null)
                return;

            PlaceCaseGenericInstances(doc, _case13Families);
        });

        if (_case13SeedError is not null)
        {
            Skip.Test(_case13SeedError);
            return;
        }


        _transactions.RunInTransaction(Doc, "Seed case 13 (audit)", doc =>
        {

            SeedCase13Items(doc, families, truth, kinds);
            if (_case13SeedError is not null)
                return;

            SeedCase13Schedules(doc);
        });

        if (_case13SeedError is not null)
        {
            Skip.Test(_case13SeedError);
            return;
        }

        VerifyCase13Seed();
    }

    private Case13Definitions? SeedCase13Definitions()
    {
        try
        {
            var file = Application.OpenSharedParameterFile();
            if (file is null)
            {
                _case13SeedError = "Временный ФОП кейса 13 не открылся";
                return null;
            }

            var group = file.Groups.Create(Case13SharedGroup);

            ExternalDefinition Create(string name, Guid guid) =>
                (ExternalDefinition)group.Definitions.Create(
                    new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)
                    {
                        GUID = guid
                    });

            return new Case13Definitions(
                Create(Case13NameParameter, Case13NameGuid),
                Create(Case13MarkParameter, Case13MarkGuid),
                Create(Case13UnitParameter, Case13UnitGuid),
                Create(Case13CountParameter, Case13CountGuid),
                Create(Case13BaitParameter, Case13BaitGuid));
        }
        catch (Exception ex)
        {
            _case13SeedError = "Не удалось засидировать определения ФОП кейса 13: " +
                               ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>4 общих параметра — type binding; приманка «Комментарий» — instance.</summary>
    private Case13Definitions BindCase13Parameters(Document doc, Case13Definitions definitions)
    {
        try
        {
            var app = doc.Application;
            var category = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
            var categorySet = app.Create.NewCategorySet();
            categorySet.Insert(category);
            var typeBinding = app.Create.NewTypeBinding(categorySet);
            var instanceBinding = app.Create.NewInstanceBinding(categorySet);
            var map = doc.ParameterBindings;

            // Обёртка резолвится в момент КАЖДОГО Insert: между двумя Insert
            // Revit может перечитывать ФОП — см. док-комментарий
            // FindCaseSharedDefinition.
            foreach (var parameterName in new[]
                     {
                         Case13NameParameter, Case13MarkParameter,
                         Case13UnitParameter, Case13CountParameter
                     })
            {
                var definition = FindCaseSharedDefinition(Case13SharedGroup, parameterName);
                if (definition is null)
                {
                    _case13SeedError = "Общий параметр «" + parameterName +
                                       "» не найден в ФОП перед привязкой (обёртка протухла)";
                    return definitions;
                }

                if (!map.Insert(definition, typeBinding))
                {
                    _case13SeedError = "Не удалось привязать общий параметр «" +
                                       definition.Name + "» (type) к категории";
                    return definitions;
                }
            }

            var bait = FindCaseSharedDefinition(Case13SharedGroup, Case13BaitParameter);
            if (bait is null)
            {
                _case13SeedError = "Приманка «" + Case13BaitParameter +
                                   "» не найдена в ФОП перед привязкой (обёртка протухла)";
                return definitions;
            }

            definitions = definitions with { Bait = bait };
            if (!map.Insert(bait, instanceBinding))
            {
                _case13SeedError = "Не удалось привязать приманку «" +
                                   bait.Name + "» (instance)";
            }
        }
        catch (Exception ex)
        {
            _case13SeedError = "Привязка общих параметров кейса 13: " +
                               ex.GetType().Name + ": " + ex.Message + " @ " + ex.StackTrace;
        }

        return definitions;
    }

    /// <summary>
    /// Единый ключ «семейство/тип» для таблицы истины кейса 13: истина
    /// индексируется и ищется ТОЛЬКО через него (ранее имена сида и ключи
    /// истины расходились из-за temp-префикса .rfa — живой прогон 2026-11-22).
    /// </summary>
    private static string Case13KeyOf(string family, string type) => family + "/" + type;

    private void SeedCase13Items(
        Document doc,
        List<CaseGenericFamilySeed> families,
        (string Family, string Type, string? Name, string? Mark, string? Unit)[] truth,
        Dictionary<string, string> kinds)
    {
        var truthByKey = new Dictionary<
            string,
            (string Family, string Type, string? Name, string? Mark, string? Unit)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in truth)
            truthByKey[Case13KeyOf(row.Family, row.Type)] = row;

        foreach (var family in families)
        {
            foreach (var type in family.Types)
            {
                if (!truthByKey.TryGetValue(
                        Case13KeyOf(family.FamilyName, type.TypeName), out var row))
                {
                    _case13SeedError = "Нет строки истины для «" +
                                       Case13KeyOf(family.FamilyName, type.TypeName) + "»";
                    return;
                }

                _case13Items.Add(new Case13ItemSeed
                {
                    Family = family.FamilyName,
                    TypeName = type.TypeName,
                    TypeId = type.TypeId,
                    InstanceId = type.InstanceId,
                    Name = row.Name,
                    Mark = row.Mark,
                    Unit = row.Unit,
                    Kind = kinds.TryGetValue(family.FamilyName, out var kind)
                        ? kind
                        : string.Empty
                });

                if (doc.GetElement(ElementIdCompat.Create(type.TypeId)) is not ElementType typeElement)
                    continue;

                // null = дырка остаётся дыркой (HasValue=false → emptyNoValue);
                // GUID-константы вместо ExternalDefinition-обёрток (протухают
                // после перечитывания ФОП — см. FindCaseSharedDefinition).
                Case13Set(typeElement, Case13NameGuid, row.Name);
                Case13Set(typeElement, Case13MarkGuid, row.Mark);
                Case13Set(typeElement, Case13UnitGuid, row.Unit);
                Case13Set(typeElement, Case13CountGuid, "1");
            }
        }

        // Приманка: «Комментарий» заполнен только у оборудования (местами пустой).
        foreach (var item in _case13Items.Where(item => item.Kind == "equipment").ToList())
        {
            if (doc.GetElement(ElementIdCompat.Create(item.InstanceId)) is { } instance)
                instance.get_Parameter(Case13BaitGuid)?.Set("черновая прикидка");
        }
    }

    private static void Case13Set(Element element, Guid guid, string? value)
    {
        if (value is null)
            return;

        element.get_Parameter(guid)?.Set(value);
    }

    private void SeedCase13Schedules(Document doc)
    {
        var sheetSchedule = ViewSchedule.CreateSchedule(
            doc, new ElementId(BuiltInCategory.OST_GenericModel));
        sheetSchedule.Name = Case13ScheduleName;
        var definition = sheetSchedule.Definition;
        foreach (var guid in new[]
                 {
                     Case13NameGuid, Case13MarkGuid, Case13UnitGuid, Case13CountGuid
                 })
        {
            if (!TryAddCaseSharedField(doc, definition, guid))
            {
                _case13SeedError = "Поле общего параметра (guid …" +
                                   guid.ToString("D").Substring(guid.ToString("D").Length - 6) +
                                   ") не найдено среди schedulable — листовая ведомость не собрана";
                return;
            }
        }

        var combined = FindCaseSchedulableFieldByName(
            doc, definition, "Семейство и типоразмер", "Family and Type");
        combined ??= FindCaseSchedulableFieldByName(doc, definition, "Семейство", "Family");
        if (combined is not null)
            definition.AddField(combined);

        var baitSchedule = ViewSchedule.CreateSchedule(
            doc, new ElementId(BuiltInCategory.OST_GenericModel));
        baitSchedule.Name = Case13BaitScheduleName;
        if (!TryAddCaseSharedField(doc, baitSchedule.Definition, Case13BaitGuid))
        {
            _case13SeedError = "Поле приманки «" + Case13BaitParameter + "» не найдено";
            return;
        }

        var titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
        if (titleBlockId is null)
        {
            var titleBlockPath = ModelSeed.FindTitleBlockFamilyFile(doc.Application.VersionNumber);
            if (titleBlockPath is not null)
            {
                doc.LoadFamily(titleBlockPath, out _);
                titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
            }
        }

        if (titleBlockId is null)
        {
            _case13SeedError = "В шаблоне нет Title Block — листовая ведомость невозможна";
            return;
        }

        var sheet = ViewSheet.Create(doc, titleBlockId);
        sheet.SheetNumber = Case13SheetNumber;
        sheet.Name = "Ведомость оборудования (лист)";
        ScheduleSheetInstance.Create(doc, sheet.Id, sheetSchedule.Id, new XYZ(0.1, 0.1, 0));
        _case13ScheduleId = sheetSchedule.Id.GetValue();
        _case13BaitScheduleId = baitSchedule.Id.GetValue();
        _case13SheetCreated = true;
    }

    private void VerifyCase13Seed()
    {
        if (!_case13SheetCreated)
        {
            Skip.Test("Листовая ведомость кейса 13 не создана");
            return;
        }

        var nameEmpty = _case13Items.Count(item => Case13IsEmpty(item, Case13NameGuid));
        var markEmpty = _case13Items.Count(item => Case13IsEmpty(item, Case13MarkGuid));
        var unitEmpty = _case13Items.Count(item => Case13IsEmpty(item, Case13UnitGuid));
        var countEmpty = _case13Items.Count(item => Case13IsEmpty(item, Case13CountGuid));
        if (_case13Items.Count != Case13ItemCount ||
            nameEmpty != 2 || markEmpty != 1 || unitEmpty != 4 || countEmpty != 0)
        {
            Skip.Test("Сид кейса 13 некорректен: позиций " + _case13Items.Count +
                      ", дырки Name/Mark/Unit/Count " + nameEmpty + "/" + markEmpty + "/" +
                      unitEmpty + "/" + countEmpty);
            return;
        }

        var sheetNumbers = AgentScheduleReader.MapSheetNumbers(Doc);
        var placed = sheetNumbers.TryGetValue(_case13ScheduleId, out var numbers) &&
                     numbers!.Count > 0;
        var baitPlaced = sheetNumbers.TryGetValue(_case13BaitScheduleId, out _);
        if (!placed || baitPlaced)
        {
            Skip.Test("Сид кейса 13 некорректен: листовая спека placed=" + placed +
                      ", приманка placed=" + baitPlaced);
            return;
        }

        var bait = Doc.GetElement(ElementIdCompat.Create(_case13BaitScheduleId)) as ViewSchedule;
        if (bait is null || !AgentScheduleReader.ReadColumns(Doc, bait, out _).Any(column =>
                ContainsText(column.Heading, Case13BaitParameter) ||
                ContainsText(column.ParameterName ?? string.Empty, Case13BaitParameter)))
        {
            Skip.Test("Сид кейса 13 некорректен: у приманки нет поля «" + Case13BaitParameter + "»");
        }
    }

    private List<string> Case13SeedSummary()
    {
        var lines = new List<string>
        {
            Case13ItemCount + " позиций generic-семейств (упрощение сида: одна категория; " +
            "значения общих параметров на ТИПАХ — type binding, у инстансов наследуются)",
            "Общие параметры проекта (temp-ФОП, группа «" + Case13SharedGroup + "»): «" +
            Case13NameParameter + "», «" + Case13MarkParameter + "», «" + Case13UnitParameter +
            "», «" + Case13CountParameter + "» (type) + «" + Case13BaitParameter + "» (instance)",
            "Листовая ведомость «" + Case13ScheduleName + "» (4 общих поля + Семейство и типоразмер) " +
            "размещена на листе " + Case13SheetNumber,
            "приманка: «" + Case13BaitScheduleName + "» ВНЕ листов, поле «" + Case13BaitParameter +
            "» — в обязательный набор попадать не должен",
            "дырки: Наименование — 2 оборудования; Единица измерения — 4 фитинга (безопасная); " +
            "Марка — 1 тип трубы"
        };
        foreach (var item in _case13Items)
        {
            lines.Add("  - «" + item.Family + "» / «" + item.TypeName + "» [" + item.Kind + "]: " +
                      "Name=" + Case13Cell(item.Name) + ", Mark=" + Case13Cell(item.Mark) +
                      ", Unit=" + Case13Cell(item.Unit));
        }

        return lines;
    }

    // ── Тулы ────────────────────────────────────────────────────────────


    // ── Чтение истины по модели ─────────────────────────────────────────

    /// <summary>Значение параметра как его видит аудит: инстанс → тип.</summary>
    private string? Case13ReadValue(Case13ItemSeed item, Guid guid)
    {
        try
        {
            // Type binding: авторитетный источник — тип. Инстанс-ветка первой быть
            // не может: get_Parameter(GUID) у инстанса возвращает унаследованное
            // представление с пустым AsString и глушит ??-переход к типу.
            var parameter = (Doc.GetElement(ElementIdCompat.Create(item.TypeId)) as ElementType)
                             ?.get_Parameter(guid) ??
                            Doc.GetElement(ElementIdCompat.Create(item.InstanceId))
                             ?.get_Parameter(guid);
            return parameter?.AsString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool Case13IsEmpty(Case13ItemSeed item, Guid guid) =>
        string.IsNullOrWhiteSpace(Case13ReadValue(item, guid));

    private static string Case13Cell(string? value) => value is null ? "(пусто)" : value;
}
