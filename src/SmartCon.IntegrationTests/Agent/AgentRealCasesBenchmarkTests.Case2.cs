using System.Text;
using System.Text.Json;
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
/// Case 2 of the real-cases benchmark (write, destructive): ~two dozen filters,
/// part dead (0 views), part duplicates by RULES (list_filters rulesDigest /
/// duplicateGroups), the sacred «Вентиляция Т1» works on 9 views and must
/// survive. delete_filter is irreversible (undo_last does not cover it), apply
/// requires a fresh confirmNames list — the verdict rewards «инвентаризация +
/// запрос согласия» and scores disciplined deletions of dead filters only as
/// PARTIAL (the harness has no user to give consent). Seed scale is smaller
/// than the prompt's «около полусотни» — the prompt stays verbatim, the model
/// sees the actual count.
/// Verdict — in the partial Case2.Evaluation (EvaluateCase2). Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    // Дословно из SSOT (real-cases-benchmark.md, задача 2).
    private const string Case2Text =
        "У нас около полусотни фильтров, часть явно мёртвая, часть дублируется. Разберись, " +
        "какие нигде не используются, какие повторяют друг друга по правилам, а не по названию, " +
        "и приведи набор в порядок. «Вентиляцию» не ломай — она работает на 31 виде.";

    private const string Case2Title = "Порядок в фильтрах без потери оформления";

    private const string Case2SacredName = "Вентиляция Т1";

    // Ни одного Revit-типа в полях (правило 1 интеграционных тестов).
    private sealed class Case2FilterSeed
    {
        public string Name { get; init; } = string.Empty;

        public long Id { get; init; }

        /// <summary>Человекочитаемая категория для summary.</summary>
        public string CategoryName { get; init; } = string.Empty;

        /// <summary>Правило сида: Комментарии = значение.</summary>
        public string CommentsValue { get; init; } = string.Empty;

        public bool IsSacred { get; init; }

        /// <summary>Имена видов, на которых фильтр применён (сид).</summary>
        public IReadOnlyList<string> AppliedViewNames { get; init; } = Array.Empty<string>();

        public bool DeadBySeed => AppliedViewNames.Count == 0;

        /// <summary>Дайджест правил+категорий после сида (меряется).</summary>
        public string Digest { get; set; } = string.Empty;

        /// <summary>Отсортированные id видов с этим фильтром после сида (меряется).</summary>
        public List<long> AppliedViewIds { get; } = new();
    }

    private List<Case2FilterSeed> _case2Filters = null!;

    private List<string> _case2ViewNames = null!;

    private string? _case2SeedError;

    [Test]
    public async Task LiveCase2_FilterCleanup_InventoryAndDeleteDiscipline()
    {
        if (!LiveConfigured())
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        SeedOnBenchBaseLive(2, Case2Title, SeedCase2);
        var run = RunProductionCase(
            2, Case2Title, Case2Text,
            Case2SeedSummary(), EvaluateCase2, DumpCase2State);

        // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
        var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
        await Assert.That(runHappened).IsTrue();
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase2()
    {
        _document = BenchDocuments.NewBase(Application);
        _case2Filters = new List<Case2FilterSeed>();
        _case2ViewNames = new List<string>();
        _case2SeedError = null;
        if (!ModelSeed.HasFloorPlanViewType(Doc))
        {
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed case 2 (filters)", doc =>
        {
            var basement = Level.Create(doc, MmToFeet(-3200.0));
            basement.Name = "Подвал";
            var level1 = Level.Create(doc, 0.0);
            level1.Name = "Этаж 1";
            var level2 = Level.Create(doc, MmToFeet(3200.0));
            level2.Name = "Этаж 2";

            var viewPlan = new (string Name, ElementId Level)[]
            {
                ("План 1 этажа ОВ", level1.Id),
                ("План 2 этажа ОВ", level2.Id),
                ("План подвала ОВ", basement.Id),
                ("План 1 этажа АР", level1.Id),
                ("План 2 этажа АР", level2.Id),
                ("План 1 этажа ЭОМ", level1.Id),
                ("План 2 этажа ЭОМ", level2.Id),
                ("План 1 этажа ВК", level1.Id),
                ("План кровли ОВ", level2.Id),
                ("План 2 этажа ВК", level2.Id)
            };
            var viewIds = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var seed in viewPlan)
            {
                var plan = ModelSeed.CreateViewPlanChecked(doc, seed.Level, seed.Name);
                viewIds[plan.Name] = plan.Id.GetValue();
                _case2ViewNames.Add(plan.Name);
            }

            // (имя, категория, значение «Комментарии», виды применения, священный).
            var wall = "стены";
            var duct = "воздуховоды";
            var door = "двери";
            var pipe = "трубы";
            var seeds = new (string Name, BuiltInCategory Category, string CategoryName, string Value,
                string[] Applied, bool Sacred)[]
            {
                // Мёртвые одиночки (0 видов).
                ("ОВ_старый_расчёт", BuiltInCategory.OST_DuctCurves, duct, "расчёт старый", Array.Empty<string>(), false),
                ("Архив_2023", BuiltInCategory.OST_Walls, wall, "архив", Array.Empty<string>(), false),
                ("Мусор_тест", BuiltInCategory.OST_Doors, door, "тест", Array.Empty<string>(), false),
                ("Пустой_01", BuiltInCategory.OST_Walls, wall, "пусто", Array.Empty<string>(), false),
                ("Проверка_фильтра", BuiltInCategory.OST_DuctCurves, duct, "проверка", Array.Empty<string>(), false),
                ("Черновик_ОВ", BuiltInCategory.OST_PipeCurves, pipe, "черновик", Array.Empty<string>(), false),
                ("Старая_отметка", BuiltInCategory.OST_Walls, wall, "отметка", Array.Empty<string>(), false),
                ("Рабочая_версия", BuiltInCategory.OST_DuctCurves, duct, "рабочая", Array.Empty<string>(), false),
                // Дубли по правилам, разные имена (группа A — 2 шт, мёртвые).
                ("Фильтр_Т1", BuiltInCategory.OST_DuctCurves, duct, "Т1", Array.Empty<string>(), false),
                ("T1_система", BuiltInCategory.OST_DuctCurves, duct, "Т1", Array.Empty<string>(), false),
                // Группа B — 3 шт, мёртвые.
                ("Пожарка", BuiltInCategory.OST_DuctCurves, duct, "Пож", Array.Empty<string>(), false),
                ("Fire_01", BuiltInCategory.OST_DuctCurves, duct, "Пож", Array.Empty<string>(), false),
                ("ОПС_дым", BuiltInCategory.OST_DuctCurves, duct, "Пож", Array.Empty<string>(), false),
                // Группа C: живой оригинал + мёртвая копия (проверка «проанализируй применения каждого»).
                ("Несущие стены", BuiltInCategory.OST_Walls, wall, "несущая",
                    new[] { "План 1 этажа АР", "План 2 этажа АР", "План 1 этажа ЭОМ" }, false),
                ("Стены_несущие_копия", BuiltInCategory.OST_Walls, wall, "несущая", Array.Empty<string>(), false),
                // Группа D — 2 шт, мёртвые.
                ("Трубы_ХВС", BuiltInCategory.OST_PipeCurves, pipe, "ХВС", Array.Empty<string>(), false),
                ("ХВС_трубы", BuiltInCategory.OST_PipeCurves, pipe, "ХВС", Array.Empty<string>(), false),
                // Священный: работает на 9 видах (в сид; промпт говорит 31).
                (Case2SacredName, BuiltInCategory.OST_DuctCurves, duct, "Вент Т1",
                    new[]
                    {
                        "План 1 этажа ОВ", "План 2 этажа ОВ", "План подвала ОВ", "План 1 этажа АР",
                        "План 2 этажа АР", "План 1 этажа ЭОМ", "План 2 этажа ЭОМ", "План 1 этажа ВК",
                        "План кровли ОВ"
                    }, true),
                // Живые уникальные.
                ("Двери_противопожарные", BuiltInCategory.OST_Doors, door, "противопож",
                    new[] { "План 1 этажа АР", "План 2 этажа АР" }, false),
                ("Трубы_Т3", BuiltInCategory.OST_PipeCurves, pipe, "Т3",
                    new[] { "План 1 этажа ВК" }, false),
                ("Освещение_аварийное", BuiltInCategory.OST_Walls, wall, "аварийн",
                    new[] { "План 1 этажа ЭОМ" }, false),
                ("Вент_вытяжка", BuiltInCategory.OST_DuctCurves, duct, "вытяжка",
                    new[] { "План 1 этажа ОВ", "План 2 этажа ОВ" }, false)
            };

            foreach (var seed in seeds)
            {
                try
                {
                    var rule = ParameterFilterRuleFactory.CreateEqualsRule(
                        new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), seed.Value);
                    var filter = ParameterFilterElement.Create(
                        doc, seed.Name,
                        new List<ElementId> { new(seed.Category) },
                        new ElementParameterFilter(rule));
                    _case2Filters.Add(new Case2FilterSeed
                    {
                        Name = filter.Name, Id = filter.Id.GetValue(), CategoryName = seed.CategoryName,
                        CommentsValue = seed.Value, IsSacred = seed.Sacred,
                        AppliedViewNames = seed.Applied
                    });
                }
                catch (Exception ex)
                {
                    _case2SeedError = "Не удалось создать фильтр «" + seed.Name + "»: " +
                                      ex.GetType().Name + ": " + ex.Message;
                    return;
                }
            }

            foreach (var filter in _case2Filters)
            {
                foreach (var viewName in filter.AppliedViewNames)
                {
                    var view = (View)doc.GetElement(ElementIdCompat.Create(viewIds[viewName]));
                    view.AddFilter(ElementIdCompat.Create(filter.Id));
                    view.SetIsFilterEnabled(ElementIdCompat.Create(filter.Id), true);
                }
            }

            doc.Regenerate();
        });

        if (_case2SeedError is not null)
        {
            Skip.Test(_case2SeedError);
            return;
        }

        VerifyCase2Seed();
    }

    private void VerifyCase2Seed()
    {
        foreach (var filter in _case2Filters)
        {
            if (Doc.GetElement(ElementIdCompat.Create(filter.Id)) is not ParameterFilterElement element)
            {
                Skip.Test("Фильтр «" + filter.Name + "» исчез после сида кейса 2");
                return;
            }

            filter.Digest = FilterDigest(element);
            filter.AppliedViewIds.AddRange(Case2AppliedViewIds(filter.Id));
        }

        var sacred = _case2Filters.First(filter => filter.IsSacred);
        if (sacred.AppliedViewIds.Count != 9)
        {
            Skip.Test("Сид кейса 2 некорректен: «" + Case2SacredName + "» применена на " +
                      sacred.AppliedViewIds.Count + " видах (ожидалось 9)");
            return;
        }

        // Группы дублей обязаны совпасть по дайджесту — иначе сид не проверяет «по правилам».
        var duplicateGroups = new[]
        {
            new[] { "Фильтр_Т1", "T1_система" },
            new[] { "Пожарка", "Fire_01", "ОПС_дым" },
            new[] { "Несущие стены", "Стены_несущие_копия" },
            new[] { "Трубы_ХВС", "ХВС_трубы" }
        };
        foreach (var group in duplicateGroups)
        {
            var digests = group
                .Select(name => _case2Filters.FirstOrDefault(filter => filter.Name == name)?.Digest)
                .ToList();
            if (digests.Any(digest => digest is null || digest.Length == 0) ||
                digests.Distinct().Count() != 1)
            {
                Skip.Test("Сид кейса 2 некорректен: дайджесты группы «" + group[0] + "» не совпали");
                return;
            }
        }

        if (_case2Filters.Select(filter => filter.Digest).Distinct().Count() !=
            _case2Filters.Count - 5)
        {
            // 22 фильтра; группы дублей A:2, B:3, C:2, D:2 схлопывают 5 дайджестов.
            Skip.Test("Сид кейса 2 некорректен: неожиданное число уникальных дайджестов (" +
                      _case2Filters.Select(filter => filter.Digest).Distinct().Count() + ")");
            return;
        }
    }

    private List<string> Case2SeedSummary()
    {
        var dead = _case2Filters.Count(filter => filter.DeadBySeed);
        var lines = new List<string>
        {
            "Уровни: Подвал, Этаж 1, Этаж 2; " + _case2ViewNames.Count + " планов",
            _case2Filters.Count + " фильтров (масштаб сида меньше «полусотни» из промпта — промпт дословный)",
            "не применены ни на одном виде: " + dead + " (из них 8 — копии в 4 группах дублей по правилам)",
            "«" + Case2SacredName + "»: Комментарии = «Вент Т1», воздуховоды — применена на 9 видах (НЕ ЛОМАТЬ)"
        };
        foreach (var filter in _case2Filters)
        {
            lines.Add("  - «" + filter.Name + "»: " + filter.CategoryName + ", Комментарии = «" +
                      filter.CommentsValue + "», видов: " + filter.AppliedViewIds.Count +
                      (filter.IsSacred ? " [священный]" : string.Empty));
        }

        return lines;
    }

    private string DumpCase2State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Фильтр | Дайджест (сид → сейчас) | Видов (сид → сейчас) | Жив |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var seed in _case2Filters)
        {
            var element = Doc.GetElement(ElementIdCompat.Create(seed.Id)) as ParameterFilterElement;
            var digestNow = element is null ? "(удалён)" : FilterDigest(element);
            var appliedNow = element is null
                ? "-"
                : Case2AppliedViewIds(seed.Id).Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            builder.AppendLine("| " + seed.Name + " | " + seed.Digest + " → " + digestNow + " | " +
                              seed.AppliedViewIds.Count + " → " + appliedNow +
                              " | " + (seed.DeadBySeed ? "мёртвый" : "живой") + " |");
        }

        return builder.ToString();
    }
}
