using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 1 of the real-cases benchmark (write, browser organization): the view
/// tree must be filed by sections — Общие данные/АР/ОВ/ВК/ЭОМ — while
/// schedules and legends stay untouched. A browser folder is the value of the
/// grouping parameter of the CURRENT organization: the seed writes that
/// parameter directly (the same mechanism set_browser_folder uses). The API
/// cannot create or switch organizations and every stock template groups by a
/// read-only property (live probe 2026-11-21: leaf «Family and Type» in all
/// RU/ADSK templates of RVT 2023/2025), so the case needs a PREPARED template
/// with a parameter-based organization — see OpenCase1DocumentOrSkip
/// (SMARTCON_CASE1_TEMPLATE / TestContent/Case1BrowserOrg.rte). Empty stub
/// folders vanish by themselves once their views are moved out — a folder
/// without views does not exist, so the verdict line is «нет видов вне папок
/// раздела». Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (real-cases-benchmark.md, задача 1).
    private const string Case1Text =
        "В проекте бардак в организации видов: часть в папках по разделам, часть свалилась " +
        "в «# По умолчанию», есть пустые папки-заглушки. Наведи единый порядок: дерево по " +
        "разделам — Общие данные, АР, ОВ, ВК, ЭОМ, — и чтобы каждый вид лежал в своей папке. " +
        "Спецификации и легенды не трогай.";

    private const string Case1Title = "Единый порядок в браузере проекта";

    private static readonly string[] Case1Sections =
        { "Общие данные", "АР", "ОВ", "ВК", "ЭОМ" };

    // Ни одного Revit-типа в полях тестового класса (правило 1 интеграционных
    // тестов, тот же механизм, что в Case4ScheduleSeed).
    private sealed class Case1ViewSeed
    {
        public string Name { get; init; } = string.Empty;

        public long Id { get; init; }

        /// <summary>Папка на момент сида: '' = «# По умолчанию».</summary>
        public string SeedFolder { get; init; } = string.Empty;

        public string Section { get; init; } = string.Empty;
    }

    private sealed class Case1Untouchable
    {
        public string Name { get; init; } = string.Empty;

        public long Id { get; init; }

        /// <summary>schedule | legend.</summary>
        public string Kind { get; init; } = string.Empty;

        public string FolderBefore { get; init; } = string.Empty;
    }

    private List<Case1ViewSeed> _case1Views = null!;

    private List<Case1Untouchable> _case1Untouchables = null!;

    /// <summary>Виды, чей параметр группировки не резолвится — из вердикта (a) исключены.</summary>
    private List<string> _case1Unresolvable = null!;

    private string? _case1SeedError;

    private int _case1UnfiledAtStart;

    [Test]
    public async Task LiveCase1_UnifiedBrowserFolders_EveryViewFiled()
    {
        var config = DogfoodConfig.TryLoad();
        if (config is null)
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        SeedCase1();
        var run = RunCase(
            1, Case1Title, Case1Text, config, CreateCase1Tools(),
            Case1SeedSummary(), EvaluateCase1, DumpCase1State);

        // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
        var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
        await Assert.That(runHappened).IsTrue();
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase1()
    {
        // Шаблон с записываемой группировкой или честный skip — см. класс-док
        // и OpenCase1DocumentOrSkip.
        OpenCase1DocumentOrSkip();

        _case1Views = new List<Case1ViewSeed>();
        _case1Untouchables = new List<Case1Untouchable>();
        _case1Unresolvable = new List<string>();
        _case1SeedError = null;
        if (!ModelSeed.HasFloorPlanViewType(Doc))
        {
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        // Имена видов уникальны: офисный шаблон может уже содержать «План 1 этажа».
        var takenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>())
            takenNames.Add(existing.Name);
        string UniqueName(string desired) =>
            takenNames.Add(desired) ? desired : desired + " (схема)";

        _transactions.RunInTransaction(Doc, "Seed case 1 (browser folders)", doc =>
        {
            var basement = Level.Create(doc, MmToFeet(-3200.0));
            basement.Name = "Подвал";
            var level1 = Level.Create(doc, 0.0);
            level1.Name = "Этаж 1";
            var level2 = Level.Create(doc, MmToFeet(3200.0));
            level2.Name = "Этаж 2";

            var planSeeds = new (string Name, ElementId Level, string Folder, string Section)[]
            {
                ("Сводный план", level1.Id, "Общие данные", "Общие данные"),
                ("План подвала", basement.Id, "АР", "АР"),
                ("План 1 этажа", level1.Id, "АР", "АР"),
                ("План 2 этажа", level2.Id, "АР", "АР"),
                ("План 1 этажа ОВ", level1.Id, "ОВ", "ОВ"),
                ("План 1 этажа ВК", level1.Id, "ВК", "ВК"),
                ("План кровли", level2.Id, string.Empty, "АР"),
                ("План 2 этажа ОВ", level2.Id, string.Empty, "ОВ"),
                ("План подвала ОВ", basement.Id, string.Empty, "ОВ"),
                ("План 2 этажа ВК", level2.Id, string.Empty, "ВК"),
                ("План 1 этажа ЭОМ", level1.Id, string.Empty, "ЭОМ"),
                ("План 2 этажа ЭОМ", level2.Id, string.Empty, "ЭОМ"),
                ("Черновик — план 1 этажа", level1.Id, "Черновики", "АР"),
                ("Проверка отметок", basement.Id, "Прочее", "Общие данные")
            };
            foreach (var seed in planSeeds)
            {
                var plan = ModelSeed.CreateViewPlanChecked(doc, seed.Level, UniqueName(seed.Name));
                _case1Views.Add(new Case1ViewSeed
                {
                    Name = plan.Name, Id = plan.Id.GetValue(),
                    SeedFolder = seed.Folder, Section = seed.Section
                });
            }

            var sectionSeeds = new (string Name, string Folder, string Section)[]
            {
                ("Разрез 1-1", "АР", "АР"),
                ("Разрез 2-2", string.Empty, "АР"),
                ("Принципиальная схема ЭОМ", string.Empty, "ЭОМ"),
                ("Старый разрез", "Черновики", "АР")
            };
            var sectionX = 0.0;
            foreach (var seed in sectionSeeds)
            {
                var section = CreateCase1Section(doc, UniqueName(seed.Name), sectionX);
                sectionX += 6.0;
                if (section is null)
                {
                    _case1SeedError = "В шаблоне нет ViewFamilyType для разрезов";
                    return;
                }

                _case1Views.Add(new Case1ViewSeed
                {
                    Name = seed.Name, Id = section.Id.GetValue(),
                    SeedFolder = seed.Folder, Section = seed.Section
                });
            }

            // Спецификации: не трогать (видны модели через list_schedules/list_views).
            var scheduleSeeds = new[]
            {
                (Name: "Ведомость отделки помещений", Category: BuiltInCategory.OST_Rooms),
                (Name: "Спецификация оборудования", Category: BuiltInCategory.OST_MechanicalEquipment),
                (Name: "Экспликация помещений 1 этажа", Category: BuiltInCategory.OST_Rooms),
                (Name: "Ведомость проёмов", Category: BuiltInCategory.OST_Doors),
                (Name: "Спецификация воздуховодов", Category: BuiltInCategory.OST_DuctCurves)
            };
            foreach (var seed in scheduleSeeds)
            {
                var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(seed.Category));
                schedule.Name = seed.Name;
            }

            // Легенды API не создаёт (CF-759): дублируем существующую, если шаблон её дал.
            var legendSource = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(view => view.ViewType == ViewType.Legend && !view.IsTemplate);
            if (legendSource is not null)
            {
                foreach (var legendName in new[] { "Легенда условных обозначений", "Обозначения ОВ" })
                {
                    var duplicated = legendSource.Duplicate(ViewDuplicateOption.Duplicate);
                    if (doc.GetElement(duplicated) is View legend)
                        legend.Name = legendName;
                }
            }

            // Папки: значение параметра группировки текущей организации видов —
            // тот же механизм, что и в set_browser_folder.
            var organization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
            if (organization is null)
            {
                _case1SeedError = "В документе нет организации браузера видов";
                return;
            }

            foreach (var seed in _case1Views)
            {
                if (doc.GetElement(ElementIdCompat.Create(seed.Id)) is not View view)
                {
                    _case1SeedError = "Вид «" + seed.Name + "» исчез сразу после создания";
                    return;
                }

                if (!SetBrowserFolderTool.TryGetGroupingParameter(
                        doc, organization, view, out var parameter, out var reason) ||
                    parameter.IsReadOnly)
                {
                    var why = reason ?? "read-only";
                    _case1SeedError = "Вид «" + seed.Name + "»: параметр группировки недоступен (" +
                                      why + ") — организация браузера не по параметру";
                    return;
                }

                if (seed.SeedFolder.Length == 0)
                    continue;
                if (!parameter.Set(seed.SeedFolder))
                {
                    _case1SeedError = "Не удалось записать папку «" + seed.SeedFolder +
                                      "» виду «" + seed.Name + "»";
                    return;
                }
            }

            doc.Regenerate();
        });

        if (_case1SeedError is not null)
        {
            Skip.Test(_case1SeedError);
            return;
        }

        VerifyCase1Seed();
    }

    private static View? CreateCase1Section(Document doc, string name, double offsetFeet)
    {
        var familyType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Section);
        if (familyType is null)
            return null;

        var transform = Transform.Identity;
        transform.BasisX = XYZ.BasisX;
        transform.BasisY = XYZ.BasisZ;
        transform.BasisZ = XYZ.BasisY;
        transform.Origin = new XYZ(offsetFeet, 0, 0);

        var box = new BoundingBoxXYZ
        {
            Transform = transform,
            Min = new XYZ(-5, 0, -5),
            Max = new XYZ(5, 3, 5)
        };
        var section = ViewSection.CreateSection(doc, familyType.Id, box);
        section.Name = name;
        return section;
    }

    private void VerifyCase1Seed()
    {
        var organization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(Doc);
        if (organization is null)
        {
            Skip.Test("Организация браузера видов исчезла после сида");
            return;
        }

        // Все организуемые виды документа (включая виды шаблона проекта) должны
        // резолвить параметр группировки — иначе вердикт (a) будет несправедлив.
        foreach (var view in Case1OrganizableViews())
        {
            if (!SetBrowserFolderTool.TryGetGroupingParameter(
                    Doc, organization, view, out var parameter, out _) || parameter.IsReadOnly)
                _case1Unresolvable.Add(view.Name);
        }

        foreach (var seed in _case1Views)
        {
            if (Doc.GetElement(ElementIdCompat.Create(seed.Id)) is not View view)
            {
                Skip.Test("Вид «" + seed.Name + "» исчез после сида кейса 1");
                return;
            }

            var folder = Case1FolderOf(Doc, organization, view);
            if (!string.Equals(folder, seed.SeedFolder, StringComparison.Ordinal))
            {
                Skip.Test("Сид кейса 1 некорректен: «" + seed.Name + "» в папке «" +
                          folder + "», ожидалась «" + seed.SeedFolder + "»");
                return;
            }
        }

        _case1UnfiledAtStart = Case1OrganizableViews()
            .Count(view => Case1FolderOf(Doc, organization, view).Length == 0);
        if (_case1UnfiledAtStart < 8)
        {
            Skip.Test("Сид кейса 1 некорректен: без папки должно остаться не меньше 8 видов, " +
                      "сейчас " + _case1UnfiledAtStart);
            return;
        }

        foreach (var untouchable in EnumerateCase1Untouchables())
        {
            var element = Doc.GetElement(ElementIdCompat.Create(untouchable.Id));
            var folder = element is View view
                ? Case1FolderOf(Doc, organization, view)
                : "(исчез)";
            _case1Untouchables.Add(new Case1Untouchable
            {
                Name = untouchable.Name, Id = untouchable.Id,
                Kind = untouchable.Kind, FolderBefore = folder
            });
        }
    }

    private string DumpCase1State()
    {
        var organization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(Doc);
        var builder = new StringBuilder();
        builder.AppendLine("| Вид | Тип | Папка сейчас | Была в сиде |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var view in Case1OrganizableViews())
        {
            var seed = _case1Views.FirstOrDefault(seed => seed.Id == view.Id.GetValue());
            builder.AppendLine("| " + view.Name + " | " + view.ViewType + " | " +
                              (organization is null ? "?" : Case1FolderOf(Doc, organization, view)) +
                              " | " + (seed?.SeedFolder ?? "(из шаблона)") + " |");
        }

        builder.AppendLine();
        builder.AppendLine("Не трогать (снапшот до → после):");
        builder.AppendLine("| Имя | Тип | Было | Стало |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var untouchable in _case1Untouchables)
        {
            var folder = organization is not null &&
                         Doc.GetElement(ElementIdCompat.Create(untouchable.Id)) is View view
                ? Case1FolderOf(Doc, organization, view)
                : "(исчез)";
            builder.AppendLine("| " + untouchable.Name + " | " + untouchable.Kind + " | " +
                              untouchable.FolderBefore + " | " + folder + " |");
        }

        return builder.ToString();
    }

    private List<string> Case1SeedSummary()
    {
        var lines = new List<string>
        {
            "Уровни: Подвал, Этаж 1, Этаж 2",
            "Виды (папка на старте):"
        };
        foreach (var seed in _case1Views)
        {
            var folder = seed.SeedFolder.Length == 0
                ? "без папки («# По умолчанию»)"
                : "папка «" + seed.SeedFolder + "»" +
                  (Array.IndexOf(Case1Sections, seed.SeedFolder) < 0 ? " (заглушка)" : string.Empty);
            lines.Add("  - «" + seed.Name + "» → " + folder + " (раздел по смыслу: " + seed.Section + ")");
        }

        var schedules = _case1Untouchables.Count(item => item.Kind == "schedule");
        var legends = _case1Untouchables.Count(item => item.Kind == "legend");
        lines.Add("Не трогать: " + schedules + " спецификаций, " + legends + " легенд");
        if (_case1Unresolvable.Count > 0)
            lines.Add("Параметр группировки не резолвится у " + _case1Unresolvable.Count +
                      " видов (из вердикта исключены)");
        return lines;
    }

    // ── Тулы ────────────────────────────────────────────────────────────

    private List<IAgentTool> CreateCase1Tools()
    {
        var context = new StubRevitContext(Doc);
        return new List<IAgentTool>
        {
            new ListViewsTool(context),
            new GetBrowserOrganizationTool(context),
            new ListSchedulesTool(context),
            new GetProjectOverviewTool(context),
            new SetBrowserFolderTool(context, _transactions!, _changeSets!),
            new SetViewParametersTool(context, _transactions!, _changeSets!),
            new RenameViewsTool(context, _transactions!, _changeSets!),
            new UndoLastTool(context, _transactions!, _changeSets!)
        };
    }

    // ── Вердикт ─────────────────────────────────────────────────────────

    private CaseVerdict EvaluateCase1(
        string? answer,
        List<BenchCall> calls,
        List<int> callsPerRound)
    {
        var redFlags = new List<string>();
        var notes = new List<string>();
        var answerText = answer ?? string.Empty;
        var sequence = calls.Count == 0
            ? "(нет вызовов)"
            : string.Join(" → ", calls.Select(call =>
                call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")")));

        // (a) Каждый не-спецификация/легенда вид — в папке одного из разделов.
        var organization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(Doc);
        var filed = 0;
        var unfiled = new List<string>();
        var badFolder = new List<string>();
        if (organization is not null)
        {
            foreach (var view in Case1OrganizableViews())
            {
                var folder = Case1FolderOf(Doc, organization, view);
                if (folder.Length == 0)
                    unfiled.Add(view.Name);
                else if (Case1Sections.Any(section =>
                             string.Equals(folder, section, StringComparison.OrdinalIgnoreCase)))
                    filed++;
                else
                    badFolder.Add(view.Name + " («" + folder + "»)");
            }
        }

        // (b) Спецификации и легенды не тронуты.
        var touchedUntouchables = new List<string>();
        if (organization is not null)
        {
            foreach (var untouchable in _case1Untouchables)
            {
                var folder = Doc.GetElement(ElementIdCompat.Create(untouchable.Id)) is View view
                    ? Case1FolderOf(Doc, organization, view)
                    : "(исчез)";
                if (!string.Equals(folder, untouchable.FolderBefore, StringComparison.Ordinal) ||
                    Doc.GetElement(ElementIdCompat.Create(untouchable.Id)) is null ||
                    !string.Equals(
                        Doc.GetElement(ElementIdCompat.Create(untouchable.Id))!.Name,
                        untouchable.Name, StringComparison.Ordinal))
                    touchedUntouchables.Add(untouchable.Name + " (" + untouchable.Kind + ": «" +
                                            untouchable.FolderBefore + "» → «" + folder + "»)");
            }
        }

        if (touchedUntouchables.Count > 0)
            redFlags.Add("тронула спецификации/легенды: " + string.Join(", ", touchedUntouchables));

        // (c) Превью «было → станет» ДО записи: dry-run вызов тулов записи с
        // непустым changed[] или явная таблица соответствия в ответе.
        var previewCall = calls.Any(call => call.Success &&
                                            call.ToolName is "set_browser_folder" or "set_view_parameters" &&
                                            IsDryRunCall(call) &&
                                            DataChangedCount(call.ResultJson) > 0);
        var answerNames = _case1Views.Count(seed => ContainsText(answerText, seed.Name));
        var answerTable = answerNames >= 5 &&
                          (answerText.Contains("→", StringComparison.Ordinal) ||
                           ContainsText(answerText, "стало") || ContainsText(answerText, "станет"));
        var preview = previewCall || answerTable;
        if (!preview)
            notes.Add("нет таблицы «было → станет» до записи (dry-run вызова с изменениями или таблицы в ответе)");

        // (d) Read-back после записи: успешный list_views после последнего apply.
        var lastApplyIndex = -1;
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            if (call.Success && !IsDryRunCall(call) &&
                call.ToolName is "set_browser_folder" or "set_view_parameters" or "rename_views")
                lastApplyIndex = index;
        }

        var readBack = lastApplyIndex < 0 || calls.Skip(lastApplyIndex + 1)
            .Any(call => call.Success && call.ToolName == "list_views");
        if (!readBack)
            notes.Add("нет read-back (list_views) после записи");

        // Запись без превью: успешный apply без единого успешного dry-run до него.
        var sawDryRun = false;
        foreach (var call in calls)
        {
            if (call.ToolName is not ("set_browser_folder" or "set_view_parameters"))
                continue;
            if (IsDryRunCall(call))
            {
                if (call.Success)
                    sawDryRun = true;
                continue;
            }

            if (call.Success && !sawDryRun)
            {
                redFlags.Add("запись без превью: apply " + call.ToolName +
                             " до любого успешного dry-run");
                break;
            }
        }

        // Красный флаг SSOT: переименование видов вместо смены папки (финальное
        // состояние: переименованный вид так и не попал в папку раздела).
        var renamedMisfiled = new List<string>();
        var renamedTotal = 0;
        foreach (var call in calls)
        {
            if (call.ToolName != "rename_views" || !call.Success || IsDryRunCall(call))
                continue;

            renamedTotal++;
            foreach (var viewId in ViewIdsFromArgs(call.ArgumentsJson))
            {
                if (Doc.GetElement(ElementIdCompat.Create(viewId)) is not View view)
                    continue;
                var folder = organization is null ? "?" : Case1FolderOf(Doc, organization, view);
                if (folder.Length == 0 ||
                    !Case1Sections.Any(section =>
                        string.Equals(folder, section, StringComparison.OrdinalIgnoreCase)))
                    renamedMisfiled.Add(view.Name + " (папка «" + folder + "»)");
            }
        }

        if (renamedMisfiled.Count > 0)
            redFlags.Add("переименовала виды вместо смены папки: " +
                         string.Join(", ", renamedMisfiled));
        else if (renamedTotal > 0)
            notes.Add("переименовала " + renamedTotal + " вид(ов), но все они лежат в папках разделов");

        // Обратная связь из стартового блока.
        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        if (_case1Unresolvable.Count > 0)
            notes.Add("у " + _case1Unresolvable.Count + " видов параметр группировки не резолвится — исключены из проверки папок");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; в папках разделов " + filed + " видов, без папки " + unfiled.Count +
                     ", в чужих папках " + badFolder.Count +
                     " (на старте без папки было " + _case1UnfiledAtStart + ")" +
                     (unfiled.Count > 0 ? "; без папки: " + string.Join(", ", unfiled) : string.Empty) +
                     (badFolder.Count > 0 ? "; чужие папки: " + string.Join(", ", badFolder) : string.Empty) +
                     "; превью " + (preview ? "да" : "нет") + ", read-back " + (readBack ? "да" : "нет") +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        var allFiled = unfiled.Count == 0 && badFolder.Count == 0;
        if (redFlags.Count == 0 && allFiled && touchedUntouchables.Count == 0 &&
            preview && readBack && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        if (redFlags.Count == 0 && touchedUntouchables.Count == 0 &&
            (unfiled.Count + badFolder.Count) <= 2 && (preview || readBack))
            return new CaseVerdict(false, 0.5, detail, redFlags);

        if (redFlags.Count == 0 && allFiled)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    // ── Вспомогательные ─────────────────────────────────────────────────
    // Case1FolderOf / Case1OrganizableViews / EnumerateCase1Untouchables и общие
    // JSON-хелперы вызовов — в AgentRealCasesBenchmarkTests.Support.cs.
}
