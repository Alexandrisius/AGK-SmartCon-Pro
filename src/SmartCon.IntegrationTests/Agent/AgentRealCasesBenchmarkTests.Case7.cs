using System.Text;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 7 of the real-cases benchmark = v2 case 2 (write, families + ФОП):
/// the seeded model carries 12 generic families split across 4 legacy shared
/// parameter sets (группы А/Б/В/Г, каждый со своими GUID — «чужие ФОПы»).
/// The target customer definitions are already bound to the category, and the
/// sheet schedule «Ведомость объёмов работ» (5 target columns, ON A SHEET) is
/// the source of the mandatory set; the draft schedule outside sheets
/// (А_* columns) is a bait. 10 placed families must be migrated (values
/// transferred by role), 2 invalids honestly reported (empty source value /
/// no source parameter), 2 unplaced families untouched and listed, plus a
/// control schedule with an empty-target filter. COST NOTE: the seed opens
/// 12 EditFamily sessions (one per family, a single transaction with all its
/// parameters) — the same count as the v1 case 5 migration seed; 1 type per
/// family keeps each session minimal. Сид — в партиале Case7.Seed, вердикт —
/// в партиале Case7.Evaluation (EvaluateCase7). Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/real-cases-benchmark-v2.md, кейс 2).
    private const string Case7Text =
        "Заказчик прислал свой файл общих параметров — вся отчётность по модели должна " +
        "собираться на его параметрах. Сейчас размещённые в модели семейства попадают " +
        "в ведомости с пустыми строчками: их общие параметры родом из старых, чужих " +
        "файлов параметров. Приведи размещённые семейства к параметрам заказчика так, " +
        "чтобы отчётность, которая идёт заказчику, собиралась заполненной: проанализируй, " +
        "какие общие параметры у семейств сейчас и из каких они наборов, замени их на " +
        "аналоги из нового файла, перенеся все заполненные значения — наименование, " +
        "марка, единицы измерения, завод-изготовитель и т.п. Работай пакетно и аккуратно: " +
        "семейств много, они собраны из разных источников, вручную это недели. Если у " +
        "семейства нужного параметра нет или значение пустое — ничего не придумывай, " +
        "собери мне отдельный список, что доделать руками, и подготовь способ быстро " +
        "увидеть остаток проблемных семейств. Семейства, не размещённые в модели, пока " +
        "не трогай — покажи их отдельным списком.";

    private const string Case7Title = "Приведение общих параметров семейств к ФОПу заказчика";

    private const string Case7SheetScheduleName = "Ведомость объёмов работ";
    private const string Case7BaitScheduleName = "Прикидка объёмов (черновик)";
    private const string Case7SheetNumber = "SC-C7-01";
    private const string Case7TargetGroupName = "ФОП Заказчика";
    private const int Case7RoleCount = 5;
    private const int Case7FamilyCount = 12;

    // Роли: 0 Наименование, 1 Марка, 2 Единица измерения, 3 Завод-изготовитель,
    // 4 Артикул. Целевые определения «ФОПа заказчика» (фиксированные GUID).
    private static readonly string[] Case7TargetNames =
    {
        "Наименование", "Марка", "Единица измерения", "Завод-изготовитель", "Артикул"
    };

    private static readonly Guid[] Case7TargetGuids =
    {
        new("5f1c2a9e-7b3d-4e08-9c41-2d8b60a7c001"),
        new("5f1c2a9e-7b3d-4e08-9c41-2d8b60a7c002"),
        new("5f1c2a9e-7b3d-4e08-9c41-2d8b60a7c003"),
        new("5f1c2a9e-7b3d-4e08-9c41-2d8b60a7c004"),
        new("5f1c2a9e-7b3d-4e08-9c41-2d8b60a7c005")
    };

    // Старые наборы (null = параметра нет в наборе группы): А — полный,
    // Б — без Завода, В — полный (у одного Наименование пустое), Г — только Марка.
    private static readonly string[] Case7OldGroupNames =
    {
        "Исходный ФОП А", "Исходный ФОП Б", "Исходный ФОП В", "Исходный ФОП Г"
    };

    private static readonly string?[][] Case7OldNames =
    {
        new[] { "А_Наименование", "А_Марка", "А_ЕдИзм", "А_Завод", "А_Артикул" },
        new string?[] { "Б_Наименование", "Б_Марка", "Б_ЕдИзм", null, "Б_Артикул" },
        new[] { "В_Наименование", "В_Марка", "В_ЕдИзм", "В_Завод", "В_Артикул" },
        new string?[] { null, "Г_Марка", null, null, null }
    };

    private static readonly Guid?[][] Case7OldGuids =
    {
        new Guid?[]
        {
            new("7a2b3c4d-5e6f-4a7b-8c1d-9e0f1a2b3c01"),
            new("7a2b3c4d-5e6f-4a7b-8c1d-9e0f1a2b3c02"),
            new("7a2b3c4d-5e6f-4a7b-8c1d-9e0f1a2b3c03"),
            new("7a2b3c4d-5e6f-4a7b-8c1d-9e0f1a2b3c04"),
            new("7a2b3c4d-5e6f-4a7b-8c1d-9e0f1a2b3c05")
        },
        new Guid?[]
        {
            new("8b3c4d5e-6f7a-4b8c-9d2e-0f1a2b3c4d11"),
            new("8b3c4d5e-6f7a-4b8c-9d2e-0f1a2b3c4d12"),
            new("8b3c4d5e-6f7a-4b8c-9d2e-0f1a2b3c4d13"),
            null,
            new("8b3c4d5e-6f7a-4b8c-9d2e-0f1a2b3c4d15")
        },
        new Guid?[]
        {
            new("9c4d5e6f-7a8b-4c9d-0e3f-1a2b3c4d5e21"),
            new("9c4d5e6f-7a8b-4c9d-0e3f-1a2b3c4d5e22"),
            new("9c4d5e6f-7a8b-4c9d-0e3f-1a2b3c4d5e23"),
            new("9c4d5e6f-7a8b-4c9d-0e3f-1a2b3c4d5e24"),
            new("9c4d5e6f-7a8b-4c9d-0e3f-1a2b3c4d5e25")
        },
        new Guid?[] { null, new("0d5e6f7a-8b9c-4d0e-1f4a-2b3c4d5e6f32"), null, null, null }
    };

    // Истина сида: null = параметра нет в наборе группы, "" = есть, но значение
    // пустое (инвалид «значение пустое»); переносимых ячеек среди размещённых — 42.
    private sealed class Case7TruthRow
    {
        public string Family { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public int Group { get; init; }

        public bool Placed { get; init; }

        public string?[] Old { get; init; } = new string?[Case7RoleCount];

        public long TypeId { get; set; }

        public long InstanceId { get; set; }

        public bool HasOld(int role) => Case7OldNames[Group][role] is not null;
    }

    private static Case7TruthRow[] Case7Truth() => new[]
    {
        new Case7TruthRow
        {
            Family = "Радиатор РС-300", Type = "РС-300-500", Group = 0, Placed = true,
            Old = new[] { "Радиатор стальной панельный", "РС-300-500", "шт", "АО Теплотех", "RT-300-500" }
        },
        new Case7TruthRow
        {
            Family = "Насос ЦН-40", Type = "ЦН-40-200", Group = 0, Placed = true,
            Old = new[] { "Насос центробежный консольный", "ЦН-40-200", "шт", "АО Гидромаш", "NS-40-200" }
        },
        new Case7TruthRow
        {
            Family = "Кран шаровой Броен", Type = "КБР-25", Group = 0, Placed = true,
            Old = new[] { "Кран шаровой муфтовый", "КБР-25", "шт", "БРОЕН Баллакс", "KB-25" }
        },
        new Case7TruthRow
        {
            Family = "Фильтр сетчатый АФ", Type = "АФ-50", Group = 0, Placed = true,
            Old = new[] { "Фильтр сетчатый фланцевый", "АФ-50", "шт", "АО Теплообменник", "AF-50" }
        },
        new Case7TruthRow
        {
            Family = "Задвижка", Type = "30с41нж100", Group = 0, Placed = false,
            Old = new[] { "Задвижка клиновая чугунная", "30с41нж100", "шт", "АО Теплообменник", "MZ-100" }
        },
        new Case7TruthRow
        {
            Family = "Вентиль запорный", Type = "В-15", Group = 1, Placed = true,
            Old = new string?[] { "Вентиль запорный муфтовый", "В-15", "шт", null, "VT-15" }
        },
        new Case7TruthRow
        {
            Family = "Обратный клапан", Type = "КОР-50", Group = 1, Placed = true,
            Old = new string?[] { "Клапан обратный поворотный", "КОР-50", "шт", null, "KOR-50" }
        },
        new Case7TruthRow
        {
            Family = "Грязевик ТС", Type = "ТС-89", Group = 1, Placed = true,
            Old = new string?[] { "Грязевик абонентский", "ТС-89", "шт", null, "TS-89" }
        },
        new Case7TruthRow
        {
            Family = "Манометр", Type = "ДМ-100", Group = 1, Placed = false,
            Old = new string?[] { "Манометр показывающий", "ДМ-100", "шт", null, "DM-100" }
        },
        new Case7TruthRow
        {
            Family = "Термометр", Type = "БТ-52", Group = 2, Placed = true,
            Old = new string?[] { "", "БТ-52", "шт", "АО Термоприбор", "BT-52" }
        },
        new Case7TruthRow
        {
            Family = "Компенсатор КСО", Type = "КСО-50-16", Group = 2, Placed = true,
            Old = new[] { "Компенсатор сильфонный осевой", "КСО-50-16", "шт", "АО Сильфон", "KSO-50" }
        },
        new Case7TruthRow
        {
            Family = "Воздухоотводчик", Type = "ВА-15С", Group = 3, Placed = true,
            Old = new string?[] { null, "ВА-15С", null, null, null }
        }
    };

    // Создаётся только внутри сида (rule 1: без статической инициализации).
    private sealed record Case7Definitions(
        ExternalDefinition[] Targets,
        ExternalDefinition?[][] Old);

    private Case7TruthRow[] _case7Truth = null!;

    private List<CaseGenericFamilySeed> _case7Families = null!;

    private SharedParameterFileScope? _case7SharedFile;

    private long _case7SheetScheduleId;

    private long _case7BaitScheduleId;

    private bool _case7SheetCreated;

    private bool _case7ViewsOnSheet;

    [Test]
    public async Task Case7_Live()
    {
        if (!LiveConfigured())
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        try
        {
            SeedOnBenchBaseLive(7, Case7Title, SeedCase7);
            var run = RunProductionCase(
                7, Case7Title, Case7Text,
                Case7SeedSummary(), EvaluateCase7, DumpCase7State);

            // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
            var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
            await Assert.That(runHappened).IsTrue();
        }
        finally
        {
            CleanupCase7();
        }
    }

    [Test]
    public async Task Case7_SeedVerifies()
    {
        try
        {
            SeedOnBenchBase("case7", SeedCase7);
            await Assert.That(_case7Truth.Length).IsEqualTo(Case7FamilyCount);
            await Assert.That(_case7Truth.Count(row => row.Placed && row.InstanceId != 0))
                .IsEqualTo(10);
            await Assert.That(_case7Truth.Count(row => !row.Placed && row.InstanceId == 0))
                .IsEqualTo(2);

            // 4 различимые сигнатуры исходных общих параметров.
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in _case7Truth)
                signatures.Add(Case7SignatureOf(row));
            await Assert.That(signatures.Count).IsEqualTo(4);

            // Переносимые ячейки = 42; цели до прогона пусты.
            var transferable = 0;
            var polluted = 0;
            foreach (var row in _case7Truth.Where(row => row.Placed))
            {
                if (Doc.GetElement(ElementIdCompat.Create(row.TypeId)) is not ElementType type)
                    continue;

                for (var role = 0; role < Case7RoleCount; role++)
                {
                    if (row.HasOld(role) && !string.IsNullOrEmpty(row.Old[role]))
                        transferable++;
                    if (!string.IsNullOrWhiteSpace(type.get_Parameter(Case7TargetGuids[role])
                            ?.AsString()))
                        polluted++;
                }
            }

            await Assert.That(transferable).IsEqualTo(42);
            await Assert.That(polluted).IsEqualTo(0);

            // Инвалид «значение пустое»: параметр есть, значение пустое.
            var thermometer = _case7Truth.Single(row => row.Family == "Термометр");
            var thermometerType = Doc.GetElement(ElementIdCompat.Create(thermometer.TypeId));
            await Assert.That(thermometerType?.get_Parameter(Case7OldGuids[2][0]!.Value))
                .IsNotNull();

            // Инвалид «параметра нет»: у Воздухоотводчика из старых — только Марка.
            var vent = _case7Truth.Single(row => row.Family == "Воздухоотводчик");
            var ventType = Doc.GetElement(ElementIdCompat.Create(vent.TypeId));
            await Assert.That(ventType?.get_Parameter(Case7OldGuids[3][1]!.Value)).IsNotNull();
            await Assert.That(ventType?.get_Parameter(Case7OldGuids[0][0]!.Value)).IsNull();

            // Неразмещённые: исходные значения на месте.
            foreach (var row in _case7Truth.Where(row => !row.Placed))
            {
                if (Doc.GetElement(ElementIdCompat.Create(row.TypeId)) is not ElementType type)
                    continue;

                for (var role = 0; role < Case7RoleCount; role++)
                {
                    if (!row.HasOld(role))
                        continue;

                    var value = type.get_Parameter(Case7OldGuids[row.Group][role]!.Value)
                        ?.AsString();
                    await Assert.That(value).IsEqualTo(row.Old[role]);
                }
            }

            // Ведомость на листе с 5 целевыми полями; приманка вне листов с А_*.
            var sheetNumbers = AgentScheduleReader.MapSheetNumbers(Doc);
            await Assert.That(sheetNumbers.TryGetValue(_case7SheetScheduleId, out var numbers) &&
                              numbers!.Count > 0).IsTrue();
            await Assert.That(sheetNumbers.TryGetValue(_case7BaitScheduleId, out _)).IsFalse();
            var sheetSchedule = Doc.GetElement(
                ElementIdCompat.Create(_case7SheetScheduleId)) as ViewSchedule;
            var bait = Doc.GetElement(
                ElementIdCompat.Create(_case7BaitScheduleId)) as ViewSchedule;
            await Assert.That(sheetSchedule).IsNotNull();
            await Assert.That(bait).IsNotNull();
            await Assert.That(Case7ColumnGuids(sheetSchedule!)
                .Count(guid => guid is not null && Case7TargetGuids.Contains(guid.Value)))
                .IsEqualTo(5);
            await Assert.That(Case7ColumnGuids(bait!)
                .Any(guid => guid == Case7OldGuids[0][0])).IsTrue();
        }
        finally
        {
            CleanupCase7();
        }
    }

    // ── Сводка сида и state read-back ───────────────────────────────────

    private List<string> Case7SeedSummary()
    {
        var lines = new List<string>
        {
            Case7FamilyCount + " generic-семейств / 4 исходных ФОП-группы (по 1 типоразмеру):",
            "- А (5 семейств, 4 размещено): полный набор {А_Наименование, А_Марка, А_ЕдИзм, " +
            "А_Завод, А_Артикул}, все значения заполнены",
            "- Б (4 семейства, 3 размещено): {Б_*} без аналога Завода — переносится то, что есть",
            "- В (2 семейства, оба размещены): {В_*} полный, у «Термометр» Наименование ПУСТОЕ " +
            "→ инвалид «значение пустое»",
            "- Г (1 семейство, размещено): только Г_Марка → инвалид «параметра нет вообще»",
            "Целевые определения «ФОП Заказчика» уже в проекте (биндинг на категорию, type): " +
            string.Join(", ", Case7TargetNames),
            "Листовая ведомость «" + Case7SheetScheduleName + "» (5 целевых полей + Семейство " +
            "и типоразмер) размещена на листе " + Case7SheetNumber +
            (_case7ViewsOnSheet ? " (на листе есть виды — модель в полировке)" : string.Empty),
            "приманка: «" + Case7BaitScheduleName + "» ВНЕ листов, поля {А_*} — на неё вестись нельзя",
            "переносимых ячеек среди размещённых — 42; инвалиды — 2; неразмещённых — 2 " +
            "(«Задвижка», «Манометр»): не трогать, показать отдельным списком"
        };
        foreach (var row in _case7Truth)
        {
            lines.Add("  - «" + row.Family + "» / «" + row.Type + "» [группа " +
                      Case7OldGroupNames[row.Group] + (row.Placed ? ", размещено" : ", НЕ размещено") +
                      "]: " + string.Join("; ", Enumerable.Range(0, Case7RoleCount)
                          .Where(row.HasOld)
                          .Select(role => Case7OldNames[row.Group][role] + "=" +
                                          (string.IsNullOrEmpty(row.Old[role]) ? "(пусто)" : row.Old[role]))));
        }

        return lines;
    }

    /// <summary>Сигнатура исходных общих параметров типа (отсортированные GUID).</summary>
    private string Case7SignatureOf(Case7TruthRow row)
    {
        var guids = new List<string>();
        if (Doc.GetElement(ElementIdCompat.Create(row.TypeId)) is ElementType type)
        {
            for (var role = 0; role < Case7RoleCount; role++)
            {
                var guid = Case7OldGuids[row.Group][role];
                if (guid is not null && type.get_Parameter(guid.Value) is not null)
                    guids.Add(guid.Value.ToString("D"));
            }
        }

        guids.Sort(StringComparer.Ordinal);
        return string.Join("|", guids);
    }

    /// <summary>GUID-ы колонок спеки (null = built-in/вычисляемое поле).</summary>
    private static List<Guid?> Case7ColumnGuids(ViewSchedule schedule)
    {
        var result = new List<Guid?>();
        try
        {
            var definition = schedule.Definition;
            foreach (var fieldId in definition.GetFieldOrder())
            {
                var field = definition.GetField(fieldId);
                if (field is null)
                    continue;

                var raw = AgentScheduleReader.DescribeParameterGuid(schedule.Document, field);
                result.Add(raw is not null && Guid.TryParse(raw, out var guid) ? guid : null);
            }
        }
        catch (Exception)
        {
            // Definition не читается — возвращаем что собрали.
        }

        return result;
    }

    private string DumpCase7State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Семейство | Размещено | Целевые значения (Наименование/Марка/ЕдИзм/Завод/Артикул) |");
        builder.AppendLine("|---|---|---|");
        foreach (var row in _case7Truth)
        {
            var cells = new List<string>();
            if (Doc.GetElement(ElementIdCompat.Create(row.TypeId)) is ElementType type)
            {
                foreach (var guid in Case7TargetGuids)
                {
                    var value = type.get_Parameter(guid)?.AsString();
                    cells.Add(string.IsNullOrWhiteSpace(value) ? "—" : value);
                }
            }

            builder.AppendLine("| " + row.Family + " | " + (row.Placed ? "да" : "нет") + " | " +
                               string.Join(" / ", cells) + " |");
        }

        builder.AppendLine();
        builder.AppendLine("Ведомости проекта:");
        var sheetNumbers = AgentScheduleReader.MapSheetNumbers(Doc);
        foreach (var schedule in AgentScheduleReader.Collect(Doc))
        {
            builder.AppendLine("- «" + schedule.Name + "» (id " + schedule.Id.GetValue() +
                               (sheetNumbers.TryGetValue(schedule.Id.GetValue(), out var numbers) &&
                                numbers!.Count > 0
                                   ? ", на листах " + string.Join(",", numbers)
                                   : ", вне листов") + ")");
        }

        return builder.ToString();
    }
}
