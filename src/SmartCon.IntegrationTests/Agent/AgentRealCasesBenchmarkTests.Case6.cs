using System.Globalization;
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
/// Case 6 of the real-cases benchmark = v2 case 1 (write, views): 5 levels and
/// 2 system view templates are seeded, plus ONE reference plan «В_Этаж 1_Отопление»
/// on the LAST level carrying the «Отопление» template — the name pattern is
/// given by the case (each project has its own), the tools never hardcode it.
/// The model must produce the 5×2 plan matrix with a REAL ViewTemplateId on
/// every plan (create + apply in one go — the owner's two manual steps), names
/// exactly «В_Этаж N_&lt;система&gt;» where N comes from the level name, leave
/// the reference untouched and create no junk. The reference already occupies
/// the «В_Этаж 1_Отопление» name, so the achievable perfect state is 10
/// pattern plans = 9 created + the reference (это и проверяет вердикт —
/// дубликаты имён видов в Revit невозможны, а эталон трогать запрещено).
/// Verdict: EvaluateCase6 below. Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (docs/agent/real-cases-benchmark-v2.md, кейс 1).
    private const string Case6Text =
        "Уровни уже скопированы в модель из координационной связи. Создай планы этажей " +
        "для моих систем: на каждом уровне — по плану каждой системы, с шаблоном вида " +
        "этой системы. Шаблон должен быть применён по-настоящему — чтобы настройки " +
        "планов управлялись из шаблона централизованно. Имена — по образцу " +
        "существующего дефолтного вида \"В_Этаж 1_Отопление\": префикс и система как в " +
        "образце, меняется только этаж (по уровню, на котором создан план).";

    private const string Case6Title = "Массовое создание планов этажей по системам";

    private const string Case6ReferenceName = "В_Этаж 1_Отопление";
    private const string Case6LevelPrefix = "Уровень ";
    private const string Case6NamePrefix = "В_Этаж ";
    private const string Case6NeutralLevelName = "SC_Служебный уровень шаблона";
    private const int Case6LevelCount = 5;

    // Ни одного Revit-типа в полях (правило 1 интеграционных тестов).
    private sealed class Case6SeedState
    {
        /// <summary>Имя системы → id шаблона вида (FloorPlan, IsTemplate).</summary>
        public readonly Dictionary<string, long> TemplateIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Номер этажа из имени уровня → id уровня.</summary>
        public readonly Dictionary<int, long> LevelIds = new();

        public long ReferenceViewId { get; set; }

        /// <summary>Все не-шаблонные виды сида (для детекта мусора после прогона).</summary>
        public readonly HashSet<long> SeedViewIds = new();

        /// <summary>Выжившие дефолтные уровни шаблона (нейтрализованы переименованием).</summary>
        public readonly List<string> LeftoverLevelNames = new();
    }

    private Case6SeedState _case6 = null!;

    private string? _case6SeedError;

    [Test]
    public async Task Case6_Live()
    {
        var config = DogfoodConfig.TryLoad();
        if (config is null)
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        SeedCase6();
        var run = RunCase(
            6, Case6Title, Case6Text, config, CreateCase6Tools(),
            Case6SeedSummary(), EvaluateCase6, DumpCase6State);

        // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
        var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
        await Assert.That(runHappened).IsTrue();
    }

    /// <summary>Гейт сида для ночных прогонов без LLM: структура сида без live-гарда.</summary>
    [Test]
    public async Task Case6_SeedVerifies()
    {
        SeedCase6();

        await Assert.That(_case6.LevelIds.Count).IsEqualTo(Case6LevelCount);
        await Assert.That(_case6.TemplateIds.Count).IsEqualTo(2);
        await Assert.That(_case6.ReferenceViewId).IsGreaterThan(0);

        foreach (var floor in Case6Floors())
        {
            var expected = Case6LevelPrefix + floor.ToString(CultureInfo.InvariantCulture);
            var level = Doc.GetElement(ElementIdCompat.Create(_case6.LevelIds[floor])) as Level;
            await Assert.That(level is not null).IsTrue();
            await Assert.That(level!.Name).IsEqualTo(expected);
        }

        foreach (var system in Case6Systems())
        {
            var template = Case6FindTemplate(system);
            await Assert.That(template is not null).IsTrue();
            await Assert.That(template!.IsTemplate).IsTrue();
            await Assert.That(template.ViewType).IsEqualTo(ViewType.FloorPlan);
        }

        // Эталон: на уровне 1 (его имя — «этаж 1»), имя эталона, шаблон «Отопление» назначен.
        var reference = Case6FindPlan(Case6ReferenceName);
        await Assert.That(reference is not null).IsTrue();
        await Assert.That(reference!.GenLevel?.Id.GetValue())
            .IsEqualTo(_case6.LevelIds[1]);
        await Assert.That(reference.ViewTemplateId.GetValue())
            .IsEqualTo(_case6.TemplateIds[Case6FirstSystem]);

        // До прогона планов по паттерну ровно один — сам эталон.
        await Assert.That(Case6PatternPlans().Count).IsEqualTo(1);
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private static string[] Case6Systems() => new[] { "Отопление", "Канализация" };

    private static string Case6FirstSystem => Case6Systems()[0];

    private static List<int> Case6Floors()
    {
        var floors = new List<int>();
        for (var floor = 1; floor <= Case6LevelCount; floor++)
            floors.Add(floor);
        return floors;
    }

    private void SeedCase6()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _case6 = new Case6SeedState();
        _case6SeedError = null;
        if (!ModelSeed.HasFloorPlanViewType(Doc))
        {
            Skip.Test("В шаблоне проекта нет ViewFamilyType для плана этажа");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed case 6 (levels, templates, reference)", doc =>
        {
            try
            {
                SeedCase6World(doc);
            }
            catch (Exception ex)
            {
                _case6SeedError = "Сид кейса 6: " + ex.GetType().Name + ": " + ex.Message;
            }
        });

        if (_case6SeedError is not null)
        {
            Skip.Test(_case6SeedError);
            return;
        }

        VerifyCase6Seed();
    }

    private void SeedCase6World(Document doc)
    {
        Case6ClearDefaultWorld(doc);

        for (var floor = 1; floor <= Case6LevelCount; floor++)
        {
            var level = ModelSeed.CreateLevel(doc, (floor - 1) * 3.0);
            level.Name = Case6LevelPrefix + floor.ToString(CultureInfo.InvariantCulture);
            _case6.LevelIds[floor] = level.Id.GetValue();
        }

        // Выжившие дефолтные уровни нейтрализуем: имя вне паттерна «Уровень N»
        // (дубликаты имён уровней Revit разрешает, но похожее имя собьёт модель
        // и верификатор — доходчиво помечаем как служебные).
        var mine = new HashSet<long>(_case6.LevelIds.Values);
        foreach (var level in new FilteredElementCollector(doc)
                     .OfClass(typeof(Level)).Cast<Level>().ToList())
        {
            if (mine.Contains(level.Id.GetValue()))
                continue;

            var oldName = level.Name;
            try
            {
                level.Name = Case6NeutralLevelName;
            }
            catch (Exception ex)
            {
                Console.WriteLine("PROBE case 6: level '" + oldName + "' not neutralized: " +
                                  ex.GetType().Name + ": " + ex.Message);
            }

            _case6.LeftoverLevelNames.Add(oldName + " → " + level.Name);
        }

        doc.Regenerate();
        var topLevelId = ElementIdCompat.Create(_case6.LevelIds[Case6LevelCount]);

        // Эталон = источник шаблона «Отопление»: CreateViewTemplate копирует
        // настройки вида (доказанный паттерн AgentBatchViewToolsTests), затем
        // шаблон назначается эталону по-настоящему (ViewTemplateId). Эталон живёт
        // на УРОВНЕ 1 — его имя должно быть согласовано с уровнем, иначе модель
        // видит «ошибку проекта» и исправляет её переименованием (live 2026-11-22).
        var reference = ModelSeed.CreateViewPlanChecked(
            doc, ElementIdCompat.Create(_case6.LevelIds[1]), Case6ReferenceName);
        var heating = reference.CreateViewTemplate();
        if (heating is null)
        {
            _case6SeedError = "CreateViewTemplate вернул null (Отопление)";
            return;
        }

        heating.Name = Case6FirstSystem;
        reference.ViewTemplateId = heating.Id;

        // Шаблон «Канализация» — из временного плана, источник удаляется.
        var source = ModelSeed.CreateViewPlanChecked(doc, topLevelId, "SC_Case6_TemplateSource");
        var drainage = source.CreateViewTemplate();
        if (drainage is null)
        {
            _case6SeedError = "CreateViewTemplate вернул null (Канализация)";
            return;
        }

        drainage.Name = Case6Systems()[1];
        doc.Delete(source.Id);

        _case6.TemplateIds[Case6FirstSystem] = heating.Id.GetValue();
        _case6.TemplateIds[Case6Systems()[1]] = drainage.Id.GetValue();
        _case6.ReferenceViewId = reference.Id.GetValue();

        doc.Regenerate();
        foreach (var view in new FilteredElementCollector(doc)
                     .OfClass(typeof(View)).Cast<View>().Where(view => !view.IsTemplate))
            _case6.SeedViewIds.Add(view.Id.GetValue());
    }

    /// <summary>
    /// Дефолтный шаблон проекта создаёт свои уровни, виды и ШАБЛОНЫ ВИДОВ —
    /// кейс требует ровно 5 уровней «Уровень 1…5» без посторонних (иначе
    /// list_levels покажет модели мусор). Порядок обязателен: сначала ВСЕ виды
    /// ВКЛЮЧАЯ шаблоны (view range шаблона плана держит уровень — живой прогон
    /// 2026-11-22 поймал выживший уровень), потом уровни ДВУМЯ проходами с
    /// регенерацией (ссылки очищаются не сразу). Отказы логируются зондом
    /// PROBE; выжившие уровни нейтрализует SeedCase6World переименованием.
    /// </summary>
    private static void Case6ClearDefaultWorld(Document doc)
    {
        foreach (var view in new FilteredElementCollector(doc)
                     .OfClass(typeof(View)).Cast<View>().ToList())
            Case6TryDelete(doc, "view/template '" + view.Name + "'", view.Id);

        Case6DeleteLevelsPass(doc);
        doc.Regenerate();
        Case6DeleteLevelsPass(doc);
        doc.Regenerate();
    }

    private static void Case6DeleteLevelsPass(Document doc)
    {
        foreach (var level in new FilteredElementCollector(doc)
                     .OfClass(typeof(Level)).Cast<Level>().ToList())
            Case6TryDelete(doc, "level '" + level.Name + "'", level.Id);
    }

    private static void Case6TryDelete(Document doc, string what, ElementId id)
    {
        try
        {
            doc.Delete(id);
        }
        catch (Exception ex)
        {
            Console.WriteLine("PROBE case 6: " + what + " not deleted: " +
                              ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void VerifyCase6Seed()
    {
        var mine = new HashSet<long>(_case6.LevelIds.Values);
        foreach (var level in new FilteredElementCollector(Doc)
                     .OfClass(typeof(Level)).Cast<Level>().ToList())
        {
            if (mine.Contains(level.Id.GetValue()))
                continue;
            if (level.Name.StartsWith(Case6LevelPrefix, StringComparison.Ordinal))
            {
                Skip.Test("Дефолтный уровень «" + level.Name + "» не удалился и не " +
                          "нейтрализовался — сид кейса 6 некорректен (см. PROBE в логе)");
                return;
            }
        }

        foreach (var templateName in Case6Systems())
        {
            if (Case6FindTemplate(templateName) is null)
            {
                Skip.Test("Шаблон вида «" + templateName + "» не создан сидом кейса 6");
                return;
            }
        }

        var reference = Case6FindPlan(Case6ReferenceName);
        if (reference is null ||
            reference.Id.GetValue() != _case6.ReferenceViewId ||
            reference.GenLevel is null ||
            reference.GenLevel.Id.GetValue() != _case6.LevelIds[1])
        {
            Skip.Test("Эталон «" + Case6ReferenceName + "» не на уровне 1 — сид кейса 6 некорректен");
            return;
        }

        if (reference.ViewTemplateId.GetValue() != _case6.TemplateIds[Case6FirstSystem])
        {
            Skip.Test("У эталона не назначен шаблон «Отопление» — сид кейса 6 некорректен");
            return;
        }

        var patterns = Case6PatternPlans();
        if (patterns.Count != 1)
        {
            Skip.Test("До прогона планов по паттерну " + patterns.Count + " (нужно ровно 1 — эталон)");
        }
    }

    // ── Сводка сида и тулы ──────────────────────────────────────────────

    private List<string> Case6SeedSummary()
    {
        var lines = new List<string>
        {
            Case6LevelCount + " уровней «" + Case6LevelPrefix + "1…" + Case6LevelCount +
            "» (дефолтные уровни/виды шаблона проекта удалены)",
            "2 шаблона видов планов: «" + Case6FirstSystem + "\" (id " +
            _case6.TemplateIds[Case6FirstSystem] + "), «" + Case6Systems()[1] + "\" (id " +
            _case6.TemplateIds[Case6Systems()[1]] + ")",
            "эталон имени «" + Case6ReferenceName + "\" (id " + _case6.ReferenceViewId +
            ") на уровне «" + Case6LevelPrefix + "1\", шаблон назначен (ViewTemplateId)",
            "других планов нет; ожидается " + Case6LevelCount + "×2 планов с именами «В_Этаж N_<система>» " +
            "(N — номер из имени уровня) и НАЗНАЧЕННЫМ ViewTemplateId своего шаблона",
            "эталон имени даётся кейсом (у каждого проекта свой) — паттерн тулы не хардкодят"
        };
        if (_case6.LeftoverLevelNames.Count > 0)
            lines.Add("технические уровни дефолтного шаблона (НЕ этажи проекта, нейтрализованы): " +
                      string.Join(", ", _case6.LeftoverLevelNames));
        return lines;
    }

    private List<IAgentTool> CreateCase6Tools()
    {
        var context = new StubRevitContext(Doc);
        var tools = new List<IAgentTool>
        {
            new GetProjectOverviewTool(context),
            new ListLevelsTool(context),
            new ListViewsTool(context),
            new ListViewTemplatesTool(context),
            new GetViewSettingsTool(context),
            new GetCurrentViewInfoTool(context),
            new QueryElementsTool(context),
            new SummarizeElementsTool(context),
            new CreateFloorPlansTool(context, _transactions!, _changeSets!),
            new CreateViewsBatchTool(context, _transactions!, _changeSets!),
            new ApplyViewTemplateTool(context, _transactions!, _changeSets!),
            new RenameViewsTool(context, _transactions!, _changeSets!),
            new UndoLastTool(context, _transactions!, _changeSets!)
        };

        AddCaseFindTools(tools);
        return tools;
    }

    // ── Локаторы планов/шаблонов ────────────────────────────────────────

    private View? Case6FindTemplate(string name)
    {
        foreach (var view in new FilteredElementCollector(Doc)
                     .OfClass(typeof(View)).Cast<View>().Where(view => view.IsTemplate))
        {
            if (string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
                return view;
        }

        return null;
    }

    private ViewPlan? Case6FindPlan(string name)
    {
        foreach (var view in new FilteredElementCollector(Doc)
                     .OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(view => !view.IsTemplate))
        {
            if (string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
                return view;
        }

        return null;
    }

    private List<ViewPlan> Case6PatternPlans()
    {
        var result = new List<ViewPlan>();
        foreach (var view in new FilteredElementCollector(Doc)
                     .OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(view => !view.IsTemplate))
        {
            if (Case6ParseName(view.Name, out _, out _))
                result.Add(view);
        }

        return result;
    }

    /// <summary>«В_Этаж 12_Отопление» → (12, «Отопление»); система из сида.</summary>
    private static bool Case6ParseName(string name, out int floor, out string system)
    {
        floor = 0;
        system = string.Empty;
        if (!name.StartsWith(Case6NamePrefix, StringComparison.Ordinal))
            return false;

        var rest = name.Substring(Case6NamePrefix.Length);
        var underscore = rest.IndexOf('_');
        if (underscore <= 0)
            return false;

        var digits = rest.Substring(0, underscore);
        if (digits.Length == 0 || !digits.All(char.IsDigit))
            return false;

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out floor))
            return false;

        system = rest.Substring(underscore + 1);
        var systemName = system;
        return Case6Systems().Any(seedSystem =>
            string.Equals(systemName, seedSystem, StringComparison.Ordinal));
    }

    // ── Вердикт и state read-back ───────────────────────────────────────

    private CaseVerdict EvaluateCase6(
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

        // (1) Матрица 5×2: имя есть + шаблон верный + этаж имени = уровню вида.
        // Эталон закрывает строку (этаж 1, Отопление): он сидирован на уровне 1,
        // так что GenLevel проверяется у всех десяти планов одинаково.
        var rowsOk = 0;
        var rowsExisting = 0;
        var missing = new List<string>();
        var wrongTemplate = new List<string>();
        var wrongLevel = new List<string>();
        foreach (var floor in Case6Floors())
        {
            foreach (var system in Case6Systems())
            {
                var expected = Case6NamePrefix + floor.ToString(CultureInfo.InvariantCulture) +
                               "_" + system;
                var view = Case6FindPlan(expected);
                if (view is null)
                {
                    missing.Add(expected);
                    continue;
                }

                rowsExisting++;
                var templateOk =
                    view.ViewTemplateId.GetValue() == _case6.TemplateIds[system];
                if (!templateOk)
                    wrongTemplate.Add(expected);

                var levelOk = view.GenLevel?.Id.GetValue() == _case6.LevelIds[floor];
                if (!levelOk)
                    wrongLevel.Add(expected);

                if (templateOk && levelOk)
                    rowsOk++;
            }
        }

        // (2) Эталон не тронут: существует, имя прежнее, шаблон прежний.
        var reference = Case6FindPlan(Case6ReferenceName);
        var referenceIntact = reference is not null &&
                              reference.Id.GetValue() == _case6.ReferenceViewId &&
                              reference.ViewTemplateId.GetValue() ==
                              _case6.TemplateIds[Case6FirstSystem];
        if (!referenceIntact)
            redFlags.Add("эталон «" + Case6ReferenceName + "» переименован или потерял шаблон");

        // (3) Мусор: новые вне-паттерна виды (дубликаты с суффиксами, черновики).
        var junk = new List<string>();
        foreach (var view in new FilteredElementCollector(Doc)
                     .OfClass(typeof(View)).Cast<View>().Where(view => !view.IsTemplate))
        {
            if (_case6.SeedViewIds.Contains(view.Id.GetValue()))
                continue;
            if (Case6ParseName(view.Name, out _, out _))
                continue;
            junk.Add(view.Name);
        }

        if (junk.Count > 0)
            redFlags.Add("созданы виды вне паттерна эталона: " + string.Join(", ", junk.Take(5)) +
                         (junk.Count > 5 ? " …" : string.Empty));

        // (4) Пакетность: >6 применённых вызовов создания = поштучно.
        var appliedCreates = calls.Count(call =>
            call.Success && !IsDryRunCall(call) &&
            call.ToolName is "create_floor_plans" or "create_views_batch");
        if (appliedCreates > 6)
            redFlags.Add("поштучное создание планов вместо батча: " + appliedCreates +
                         " применённых вызовов создания");

        if (wrongTemplate.Count >= 3)
            redFlags.Add("шаблон вида не назначен у " + wrongTemplate.Count + " планов (" +
                         string.Join(", ", wrongTemplate.Take(3)) + "…)");

        if (missing.Count > 0)
            notes.Add("нет планов: " + string.Join(", ", missing));
        if (wrongTemplate.Count > 0 && wrongTemplate.Count < 3)
            notes.Add("без шаблона: " + string.Join(", ", wrongTemplate));
        if (wrongLevel.Count > 0)
            notes.Add("этаж имени ≠ уровню вида: " + string.Join(", ", wrongLevel));

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; планов по паттерну " + rowsOk + "/" + Case6LevelCount * 2 +
                     " (существует " + rowsExisting + "), эталон " +
                     (referenceIntact ? "не тронут" : "ИЗМЕНЁН") +
                     ", создание пакетно " + (appliedCreates <= 6 ? "да" : "нет (" + appliedCreates + ")") +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (redFlags.Count == 0 && rowsOk == Case6LevelCount * 2 && referenceIntact && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // 0.5 = ≥60% матрицы (планы + шаблоны) — шкала SSOT кейса.
        if (rowsOk >= 6)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    private string DumpCase6State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| План | Есть | Шаблон | GenLevel |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var floor in Case6Floors())
        {
            foreach (var system in Case6Systems())
            {
                var expected = Case6NamePrefix + floor.ToString(CultureInfo.InvariantCulture) +
                               "_" + system;
                var view = Case6FindPlan(expected);
                var template = view is null || view.ViewTemplateId.GetValue() <= 0
                    ? "—"
                    : Doc.GetElement(view.ViewTemplateId)?.Name ?? "—";
                var genLevel = view?.GenLevel?.Id.GetValue() ?? 0;
                var levelName = genLevel == 0
                    ? "—"
                    : Doc.GetElement(ElementIdCompat.Create(genLevel))?.Name ?? "—";
                builder.AppendLine("| " + expected + " | " + (view is null ? "нет" : "id " + view.Id.GetValue()) +
                                  " | " + template + " | " + levelName + " |");
            }
        }

        return builder.ToString();
    }
}
