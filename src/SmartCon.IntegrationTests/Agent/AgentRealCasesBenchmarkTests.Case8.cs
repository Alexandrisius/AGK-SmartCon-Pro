using System.Text;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 8 of the real-cases benchmark = v2 case 3 (write, schedules): the
/// seeded model pretends the case 7 migration already happened — 6 generic
/// families carry BOTH the old ADSK set (группа А GUIDs, см. кейс 7) and the
/// customer targets, all filled. Three ГОСТ-styled schedules live on sheets
/// (the big one: 9 columns incl. a hidden column, custom headings/widths,
/// 2-level sorting with flags, filter, grouping with totals, striped rows;
/// the middle and simple ones share a second sheet), plus a draft bait
/// outside sheets. The agent must rebuild every sheet schedule on the
/// customer parameters (clone, one-to-one settings), give the new one the
/// old name and place, keep the old alive renamed with «Архив» and off the
/// sheet, skip the no-analog column and report it. Сид — в партиале
/// Case8.Seed, вердикт — в партиале Case8.Evaluation (EvaluateCase8).
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/real-cases-benchmark-v2.md, кейс 3).
    private const string Case8Text =
        "Заказчик прислал свой файл общих параметров — вся отчётность должна собираться " +
        "на его параметрах. Отчётность в модели уже настроена и оформлена по ГОСТу: " +
        "ведомости вынесены на листы, у каждой настроены колонки с ширинами и " +
        "заголовками, скрытые столбцы, сортировки, фильтры, группировки с итогами, " +
        "стиль таблиц, своё положение на листе. Переведи всю отчётность, которая идёт " +
        "заказчику, на параметры из его файла: для каждой такой ведомости собери точный " +
        "аналог на новых параметрах, перенеся оформление и настройки один-в-один — " +
        "порядок и заголовки колонок, ширины, скрытые столбцы, сортировку, фильтры, " +
        "группировки и итоги, стиль таблиц. Новая ведомость должна получить имя старой " +
        "и встать на листе ровно на её место. Саму старую не удаляй и не оставляй на " +
        "листе — переименуй её как архивную (с пометкой «Архив» в имени), чтобы я мог " +
        "подсматривать, как она была настроена, если в новой что-то упустим. Если у " +
        "какого-то поля не находится аналога в новом файле параметров — колонку пропусти " +
        "и покажи мне список таких мест.";

    private const string Case8Title =
        "Перевод спецификаций на ФОП заказчика (замена на месте, старые — в архив)";

    private const string Case8BigName = "Ведомость оборудования (ГОСТ)";
    private const string Case8MiddleName = "Ведомость материалов";
    private const string Case8SimpleName = "Ведомость арматуры";
    private const string Case8BaitName = "Черновик сметы (для себя)";
    private const string Case8BigSheetNumber = "СК-ВОР-01";
    private const string Case8SharedSheetNumber = "СК-ВОР-02";
    private const string Case8OldGroupName = "АДСК (старый набор)";

    // Колонка БЕЗ аналога в ФОПе заказчика (ожидаем пропуск + упоминание в отчёте).
    private const string Case8NoteName = "А_Примечание";
    private static readonly Guid Case8NoteGuid = new("1e6f7a8b-9c0d-4e1f-2a5b-3c4d5e6f7f41");

    // Элементы: старый набор (группа А, см. кейс 7) и целевой заполнены у всех —
    // «как будто миграция кейса 2 уже прошла».
    private sealed class Case8TruthRow
    {
        public string Family { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public string[] Old { get; init; } = new string[Case7RoleCount];

        public string[] New { get; init; } = new string[Case7RoleCount];

        public long TypeId { get; set; }

        public long InstanceId { get; set; }
    }

    private static Case8TruthRow[] Case8Truth() => new[]
    {
        new Case8TruthRow
        {
            Family = "Насос К-20", Type = "К-20",
            Old = new[] { "Насос центробежный К-20", "К-20", "шт", "АО Гидромаш", "NK-20" },
            New = new[] { "Насос центробежный К-20", "К-20", "шт", "АО Гидромаш", "NK-20" }
        },
        new Case8TruthRow
        {
            Family = "Насос К-45", Type = "К-45",
            Old = new[] { "Насос центробежный К-45", "К-45", "шт", "АО Гидромаш", "NK-45" },
            New = new[] { "Насос центробежный К-45", "К-45", "шт", "АО Гидромаш", "NK-45" }
        },
        new Case8TruthRow
        {
            Family = "Кран шаровой", Type = "Ду25",
            Old = new[] { "Кран шаровой муфтовый", "Ду25", "шт", "БРОЕН Баллакс", "KB-25" },
            New = new[] { "Кран шаровой муфтовый", "Ду25", "шт", "БРОЕН Баллакс", "KB-25" }
        },
        new Case8TruthRow
        {
            Family = "Задвижка", Type = "Ду50",
            Old = new[] { "Задвижка клиновая", "Ду50", "шт", "АО Арматура", "MZ-50" },
            New = new[] { "Задвижка клиновая", "Ду50", "шт", "АО Арматура", "MZ-50" }
        },
        new Case8TruthRow
        {
            Family = "Фильтр сетчатый", Type = "Ф-50",
            Old = new[] { "Фильтр сетчатый фланцевый", "Ф-50", "шт", "АО Теплообменник", "AF-50" },
            New = new[] { "Фильтр сетчатый фланцевый", "Ф-50", "шт", "АО Теплообменник", "AF-50" }
        },
        new Case8TruthRow
        {
            Family = "Грязевик", Type = "ТС-89",
            Old = new[] { "Грязевик абонентский", "ТС-89", "шт", "АО Теплообменник", "TS-89" },
            New = new[] { "Грязевик абонентский", "ТС-89", "шт", "АО Теплообменник", "TS-89" }
        }
    };

    // Создаётся только внутри сида (rule 1: без статической инициализации).
    private sealed record Case8Definitions(
        ExternalDefinition[] Targets,
        ExternalDefinition[] Old,
        ExternalDefinition Note);

    private sealed class Case8Placement
    {
        public string Name { get; init; } = string.Empty;

        public long ScheduleId { get; init; }

        public string SheetNumber { get; init; } = string.Empty;

        public double PointMmX { get; init; }

        public double PointMmY { get; init; }
    }

    private Case8TruthRow[] _case8Truth = null!;

    private List<CaseGenericFamilySeed> _case8Families = null!;

    private SharedParameterFileScope? _case8SharedFile;

    private List<Case8Placement> _case8Placements = null!;

    private long _case8BaitId;

    private bool _case8SheetsCreated;

    [Test]
    public async Task Case8_Live()
    {
        var config = DogfoodConfig.TryLoad();
        if (config is null)
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        try
        {
            SeedCase8();
            var run = RunCase(
                8, Case8Title, Case8Text, config, CreateCase8Tools(),
                Case8SeedSummary(), EvaluateCase8, DumpCase8State);

            // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
            var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
            await Assert.That(runHappened).IsTrue();
        }
        finally
        {
            CleanupCase8();
        }
    }

    [Test]
    public async Task Case8_SeedVerifies()
    {
        try
        {
            SeedCase8();
            await Assert.That(_case8Truth.Length).IsEqualTo(6);
            await Assert.That(_case8Truth.Count(row => row.InstanceId != 0)).IsEqualTo(6);

            // Оба набора параметров заполнены у каждого типоразмера.
            foreach (var row in _case8Truth)
            {
                if (Doc.GetElement(ElementIdCompat.Create(row.TypeId)) is not ElementType type)
                    continue;

                for (var role = 0; role < Case7RoleCount; role++)
                {
                    await Assert.That(
                            string.IsNullOrWhiteSpace(
                                type.get_Parameter(Case7OldGuids[0][role]!.Value)?.AsString()))
                        .IsFalse();
                    await Assert.That(
                            string.IsNullOrWhiteSpace(
                                type.get_Parameter(Case7TargetGuids[role])?.AsString()))
                        .IsFalse();
                }
            }

            // 3 листовые спеки: большая отдельно, средняя+простая на одном листе.
            var sheetNumbers = AgentScheduleReader.MapSheetNumbers(Doc);
            var big = _case8Placements.Single(item => item.Name == Case8BigName);
            var middle = _case8Placements.Single(item => item.Name == Case8MiddleName);
            var simple = _case8Placements.Single(item => item.Name == Case8SimpleName);
            await Assert.That(sheetNumbers.TryGetValue(big.ScheduleId, out var bigSheets) &&
                              bigSheets!.Contains(Case8BigSheetNumber)).IsTrue();
            await Assert.That(sheetNumbers.TryGetValue(middle.ScheduleId, out var middleSheets) &&
                              middleSheets!.Contains(Case8SharedSheetNumber)).IsTrue();
            await Assert.That(sheetNumbers.TryGetValue(simple.ScheduleId, out var simpleSheets) &&
                              simpleSheets!.Contains(Case8SharedSheetNumber)).IsTrue();
            await Assert.That(sheetNumbers.TryGetValue(_case8BaitId, out _)).IsFalse();

            // Настройки большой спеки: колонки, скрытая, 2 сортировки, фильтр, итоги.
            var bigSchedule = Doc.GetElement(ElementIdCompat.Create(big.ScheduleId)) as ViewSchedule;
            await Assert.That(bigSchedule).IsNotNull();
            var definition = bigSchedule!.Definition;
            await Assert.That(definition.GetFieldOrder().Count)
                .IsGreaterThanOrEqualTo(7);
            var hidden = 0;
            foreach (var fieldId in definition.GetFieldOrder())
                if (definition.GetField(fieldId)?.IsHidden == true)
                    hidden++;
            await Assert.That(hidden).IsGreaterThanOrEqualTo(1);
            await Assert.That(definition.GetSortGroupFields().Count).IsEqualTo(2);
            await Assert.That(definition.GetFilters().Count).IsEqualTo(1);
            await Assert.That(definition.ShowGrandTotal).IsTrue();

            // Приманка: старое поле (Марка АДСК) присутствует.
            var bait = Doc.GetElement(ElementIdCompat.Create(_case8BaitId)) as ViewSchedule;
            await Assert.That(bait).IsNotNull();
            await Assert.That(Case7ColumnGuids(bait!)
                .Any(guid => guid == Case7OldGuids[0][1])).IsTrue();
        }
        finally
        {
            CleanupCase8();
        }
    }

    // ── Сводка сида и state read-back ───────────────────────────────────

    private List<string> Case8SeedSummary()
    {
        var lines = new List<string>
        {
            "6 generic-семейств (по 1 типоразмеру, размещены): у каждого заполнены ОБА набора — " +
            "старый «АДСК» (" + string.Join(", ", Case7OldNames[0].Where(name => name is not null)) +
            ") и целевой «ФОП Заказчика» (" + string.Join(", ", Case7TargetNames) + ")",
            "«" + Case8BigName + "» — ГОСТ: 9 колонок (5 АДСК + Семейство + Тип + Кол-во + " +
            Case8NoteName + " [БЕЗ аналога]), скрытый столбец Завода, свои ширины/заголовки, " +
            "сортировка 2 уровня с итогами и пустой строкой, фильтр Ед.изм. = шт, общий итог, " +
            "полосатые строки — на листе " + Case8BigSheetNumber,
            "«" + Case8MiddleName + "» (5 колонок) и «" + Case8SimpleName + "» (3 колонки) — " +
            "обе на листе " + Case8SharedSheetNumber + " (лист с несколькими ведомостями)",
            "приманка: «" + Case8BaitName + "» ВНЕ листов, старые поля — не трогать",
            "все 3 листовые спеки перевести на целевые параметры: новая получает имя старой " +
            "и её место на листе (±2 мм), старая жива с «Архив» в имени и снята с листа; " +
            "колонку без аналога (" + Case8NoteName + ") пропустить и перечислить"
        };
        foreach (var placement in _case8Placements)
        {
            lines.Add("  - «" + placement.Name + "» → лист " + placement.SheetNumber +
                      ", точка " + placement.PointMmX.ToString(
                          System.Globalization.CultureInfo.InvariantCulture) + ";" +
                      placement.PointMmY.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                      " мм");
        }

        return lines;
    }

    private string DumpCase8State()
    {
        var builder = new StringBuilder();
        foreach (var placement in _case8Placements)
        {
            builder.AppendLine("## «" + placement.Name + "» (сид-лист " + placement.SheetNumber +
                               ", точка " + placement.PointMmX + ";" + placement.PointMmY + " мм)");
            builder.AppendLine(Case8DescribeSchedule(placement.ScheduleId));
        }

        builder.AppendLine("## Приманка (вне листов)");
        builder.AppendLine(Case8DescribeSchedule(_case8BaitId));
        return builder.ToString();
    }

    private string Case8DescribeSchedule(long scheduleId)
    {
        var builder = new StringBuilder();
        if (Doc.GetElement(ElementIdCompat.Create(scheduleId)) is not ViewSchedule schedule)
            return "(спека не найдена — id " + scheduleId + ")\n";

        try
        {
            var definition = schedule.Definition;
            foreach (var fieldId in definition.GetFieldOrder())
            {
                var field = definition.GetField(fieldId);
                if (field is null)
                    continue;

                var raw = AgentScheduleReader.DescribeParameterGuid(Doc, field);
                builder.AppendLine("- " + field.GetName() + " | заголовок «" + field.ColumnHeading +
                                   "» | " + (field.IsHidden ? "скрыта" : "видима") +
                                   " | guid " + (raw ?? "—"));
            }

            builder.AppendLine("сортировок " + definition.GetSortGroupFields().Count +
                               ", фильтров " + definition.GetFilters().Count +
                               ", общий итог " + (definition.ShowGrandTotal ? "да" : "нет") +
                               ", полосатые строки " + (schedule.HasStripedRows ? "да" : "нет"));
        }
        catch (Exception ex)
        {
            builder.AppendLine("(definition не читается: " + ex.GetType().Name + ")");
        }

        foreach (var (sheetNumber, xMm, yMm) in Case8PlacementsOf(scheduleId))
            builder.AppendLine("вынос: лист " + sheetNumber + ", " + xMm.ToString("0") + ";" +
                               yMm.ToString("0") + " мм");

        return builder.ToString();
    }

    private List<(string SheetNumber, double XMm, double YMm)> Case8PlacementsOf(long scheduleId)
    {
        var result = new List<(string, double, double)>();
        foreach (var element in new FilteredElementCollector(Doc)
                     .OfClass(typeof(ScheduleSheetInstance)))
        {
            if (element is not ScheduleSheetInstance instance ||
                instance.ScheduleId.GetValue() != scheduleId)
                continue;

            var sheet = Doc.GetElement(instance.OwnerViewId) as ViewSheet;
            result.Add((
                sheet?.SheetNumber ?? "?",
                AgentUnitsFormat.ToMillimeters(instance.Point.X),
                AgentUnitsFormat.ToMillimeters(instance.Point.Y)));
        }

        return result;
    }
}
