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
/// Case 3 of the real-cases benchmark (write, view templates): every floor plan
/// gets its section template, but three working views must keep their own view
/// range (top +3000 / cut +1400). The template range (+3700/+1150) is distinct
/// both from the target and from the default range of a fresh plan, so the
/// verdict distinguishes all three states. Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private const string Case3Text =
        "Поставь всем планам этажа шаблон их раздела, но у трёх рабочих видов диапазон вида " +
        "должен остаться свой: верх +3000, сечение +1400. Разберись, как это совместить, " +
        "и сделай.";

    private const string Case3Title = "Единые шаблоны, но у трёх видов свой диапазон";

    private const double TemplateTopMm = 3700.0;
    private const double TemplateCutMm = 1150.0;
    private const double WorkingTopMm = 3000.0;
    private const double WorkingCutMm = 1400.0;
    private const double MmTolerance = 0.5;

    private sealed class Case3ViewSeed
    {
        public string Name { get; init; } = string.Empty;

        public long Id { get; init; }

        public string Section { get; init; } = string.Empty;

        public bool IsWorking { get; init; }

        public string TemplateName => "Шаблон_" + Section;
    }

    private List<Case3ViewSeed> _case3Views = null!;

    private Dictionary<string, long> _case3Templates = null!;

    private string? _case3SeedError;

    [Test]
    public async Task LiveCase3_SectionTemplates_WorkingViewsKeepOwnRange()
    {
        if (!LiveConfigured())
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        SeedOnBenchBaseLive(3, Case3Title, SeedCase3);
        var run = RunProductionCase(
            3, Case3Title, Case3Text,
            Case3SeedSummary(), EvaluateCase3, DumpCase3State);

        // Мягкий ассерт: прогон вообще состоялся (есть ответ модели или вызовы).
        var runHappened = run.Calls.Count > 0 || !string.IsNullOrWhiteSpace(run.Answer);
        await Assert.That(runHappened).IsTrue();
    }

    // ── Сид ─────────────────────────────────────────────────────────────

    private void SeedCase3()
    {
        _document = BenchDocuments.NewBase(Application);
        _case3Views = new List<Case3ViewSeed>();
        _case3Templates = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _case3SeedError = null;
        if (!ModelSeed.HasFloorPlanViewType(Doc))
        {
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");
            return;
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed case 3 (view templates)", doc =>
        {
            var level1 = Level.Create(doc, 0.0);
            level1.Name = "Этаж 1";
            var level2 = Level.Create(doc, MmToFeet(3200.0));
            level2.Name = "Этаж 2";

            var sections = new[] { "АР", "ОВ", "ВК" };

            // Планы 1 этажа + шаблоны разделов на их основе.
            foreach (var section in sections)
            {
                var source = ModelSeed.CreateViewPlanChecked(doc, level1.Id, "План 1 этажа " + section);
                if (source.CreateViewTemplate() is not ViewPlan template)
                {
                    _case3SeedError = "CreateViewTemplate не дал ViewPlan для раздела " + section;
                    return;
                }

                template.Name = "Шаблон_" + section;
                SetPlanRange(template, level1.Id, TemplateTopMm, TemplateCutMm, ref _case3SeedError);
                _case3Templates[template.Name] = template.Id.GetValue();
                _case3Views.Add(new Case3ViewSeed
                {
                    Name = source.Name, Id = source.Id.GetValue(), Section = section
                });

                // Часть планов остаётся без шаблона.
                source.ViewTemplateId = ElementId.InvalidElementId;
            }

            // Планы 2 этажа.
            foreach (var section in sections)
            {
                var plan = ModelSeed.CreateViewPlanChecked(doc, level2.Id, "План 2 этажа " + section);
                _case3Views.Add(new Case3ViewSeed
                {
                    Name = plan.Name, Id = plan.Id.GetValue(), Section = section
                });
            }

            // Рабочие виды с уже выставленным своим диапазоном.
            var workingViews = new[]
            {
                (Name: "План 1 этажа ОВ — монтаж", Level: level1, Section: "ОВ"),
                (Name: "План 2 этажа ОВ — монтаж", Level: level2, Section: "ОВ"),
                (Name: "План 1 этажа АР — монтаж", Level: level1, Section: "АР")
            };
            foreach (var working in workingViews)
            {
                var plan = ModelSeed.CreateViewPlanChecked(doc, working.Level.Id, working.Name);
                SetPlanRange(plan, working.Level.Id, WorkingTopMm, WorkingCutMm, ref _case3SeedError);
                _case3Views.Add(new Case3ViewSeed
                {
                    Name = plan.Name, Id = plan.Id.GetValue(), Section = working.Section,
                    IsWorking = true
                });
            }

            // Часть планов уже с шаблоном своего раздела.
            AssignTemplate(doc, "План 1 этажа АР", "Шаблон_АР");
            AssignTemplate(doc, "План 2 этажа ВК", "Шаблон_ВК");

            doc.Regenerate();
        });

        if (_case3SeedError is not null)
        {
            Skip.Test(_case3SeedError);
            return;
        }

        VerifyCase3Seed();
    }

    private void AssignTemplate(Document doc, string viewName, string templateName)
    {
        var view = _case3Views.First(v => string.Equals(v.Name, viewName, StringComparison.Ordinal));
        var templateId = ElementIdCompat.Create(_case3Templates[templateName]);
        if (doc.GetElement(ElementIdCompat.Create(view.Id)) is ViewPlan plan)
            plan.ViewTemplateId = templateId;
    }

    private static void SetPlanRange(
        ViewPlan plan,
        ElementId levelId,
        double topMm,
        double cutMm,
        ref string? error)
    {
        try
        {
            var range = plan.GetViewRange();
            range.SetLevelId(PlanViewPlane.TopClipPlane, levelId);
            range.SetOffset(PlanViewPlane.TopClipPlane, MmToFeet(topMm));
            range.SetLevelId(PlanViewPlane.CutPlane, levelId);
            range.SetOffset(PlanViewPlane.CutPlane, MmToFeet(cutMm));
            plan.SetViewRange(range);
        }
        catch (Exception ex)
        {
            error ??= "Не удалось задать диапазон вида «" + plan.Name + "»: " +
                      ex.GetType().Name + ": " + ex.Message;
        }
    }

    private void VerifyCase3Seed()
    {
        foreach (var seed in _case3Views)
        {
            if (Doc.GetElement(ElementIdCompat.Create(seed.Id)) is not ViewPlan plan)
            {
                Skip.Test("Вид «" + seed.Name + "» исчез после сида кейса 3");
                return;
            }

            var (topMm, cutMm) = ReadPlanOffsets(plan);
            if (seed.IsWorking &&
                (Math.Abs(topMm - WorkingTopMm) > MmTolerance ||
                 Math.Abs(cutMm - WorkingCutMm) > MmTolerance))
            {
                Skip.Test("Рабочий вид «" + seed.Name + "» не получил свой диапазон: верх " +
                          topMm.ToString("F0") + ", сечение " + cutMm.ToString("F0"));
                return;
            }
        }

        foreach (var (name, id) in _case3Templates)
        {
            if (Doc.GetElement(ElementIdCompat.Create(id)) is not ViewPlan template)
            {
                Skip.Test("Шаблон «" + name + "» исчез после сида кейса 3");
                return;
            }

            var (topMm, cutMm) = ReadPlanOffsets(template);
            if (Math.Abs(topMm - TemplateTopMm) > MmTolerance ||
                Math.Abs(cutMm - TemplateCutMm) > MmTolerance)
            {
                Skip.Test("Шаблон «" + name + "» не получил диапазон " + TemplateTopMm + "/" +
                          TemplateCutMm + ": верх " + topMm.ToString("F0") + ", сечение " +
                          cutMm.ToString("F0"));
                return;
            }
        }
    }

    private List<string> Case3SeedSummary()
    {
        var lines = new List<string>();
        foreach (var seed in _case3Views)
        {
            lines.Add("план «" + seed.Name + "» (id " + seed.Id + ", раздел " + seed.Section +
                      (seed.IsWorking
                          ? ", рабочий: свой диапазон верх +" + WorkingTopMm.ToString("F0") +
                            "/сечение +" + WorkingCutMm.ToString("F0")
                          : string.Empty) + ")");
        }

        foreach (var (name, id) in _case3Templates)
            lines.Add("шаблон «" + name + "» (id " + id + ", диапазон верх +" +
                      TemplateTopMm.ToString("F0") + "/сечение +" + TemplateCutMm.ToString("F0") + ")");
        lines.Add("уже с шаблоном: «План 1 этажа АР» → Шаблон_АР, «План 2 этажа ВК» → Шаблон_ВК");
        return lines;
    }

    // ── Тулы ────────────────────────────────────────────────────────────


    // ── Вердикт ─────────────────────────────────────────────────────────

    /// <summary>
    /// Политика вердикта (решение владельца, #445): продукт не ограничивает модель
    /// в ходах, поэтому «не успела за cap» — артефакт харнесса, не модели. Путь
    /// «отвязала viewRange у шаблона раздела → применила шаблон → явно довела
    /// не-рабочие виды до диапазона шаблона (3700/1150) через set_view_range» —
    /// ЛЕГИТМНАЯ стратегия: при снятом include Revit не синхронизирует диапазон
    /// автоматически, и явная запись — корректное завершение. Красный флаг SSOT
    /// «отвязывает настройку у шаблона и ломает остальные виды» — только отвязка
    /// БЕЗ последующего восстановления не-рабочих видов. Провал по диапазону
    /// не-рабочих — лишь если вид остался на дефолтных 2300/1200 и модель не
    /// заметила/не починила это.
    /// </summary>
    private CaseVerdict EvaluateCase3(
        string? answer,
        List<BenchCall> calls,
        List<int> callsPerRound)
    {
        var redFlags = new List<string>();
        var notes = new List<string>();
        var problems = new List<string>();
        var pending = new List<string>();
        var answerText = answer ?? string.Empty;
        var sequence = calls.Count == 0
            ? "(нет вызовов)"
            : string.Join(" → ", calls.Select(call =>
                call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")")));

        var fixAttempts = CollectTemplateRangeFixAttempts(calls);
        var detachedTemplateIds = CollectDetachedViewRangeTemplates(calls);

        // (a)+(b)+(c): каждый план — со своим шаблоном раздела и правильным
        // диапазоном; (d) шаблоны не тронуты. Не-рабочий вид с несинхронизированным
        // диапазоном при снятом include — не провал, если модель довела его до
        // 3700/1150 (или как минимум начала чинить — тогда pending, не problem).
        var correct = 0;
        foreach (var seed in _case3Views)
        {
            if (Doc.GetElement(ElementIdCompat.Create(seed.Id)) is not ViewPlan plan)
            {
                problems.Add("«" + seed.Name + "» исчез из модели");
                continue;
            }

            var templateName = plan.ViewTemplateId == ElementId.InvalidElementId
                ? null
                : Doc.GetElement(plan.ViewTemplateId)?.Name;
            var (topMm, cutMm) = ReadPlanOffsets(plan);
            var expectedTop = seed.IsWorking ? WorkingTopMm : TemplateTopMm;
            var expectedCut = seed.IsWorking ? WorkingCutMm : TemplateCutMm;
            var templateOk = string.Equals(templateName, seed.TemplateName, StringComparison.Ordinal);
            var rangeOk = Math.Abs(topMm - expectedTop) <= MmTolerance &&
                          Math.Abs(cutMm - expectedCut) <= MmTolerance;
            if (templateOk && rangeOk)
            {
                correct++;
                continue;
            }

            var description = "«" + seed.Name + "»: шаблон " + (templateName ?? "нет") +
                              " (ожидался " + seed.TemplateName + "), верх " +
                              topMm.ToString("F0") + " (ожидался " + expectedTop.ToString("F0") +
                              "), сечение " + cutMm.ToString("F0") + " (ожидался " +
                              expectedCut.ToString("F0") + ")";
            if (!seed.IsWorking && templateOk && fixAttempts.Contains(seed.Id))
                pending.Add(description + " — модель начала чинить set_view_range (3700/1150), но не завершила");
            else
                problems.Add(description);
        }

        foreach (var (name, id) in _case3Templates)
        {
            if (Doc.GetElement(ElementIdCompat.Create(id)) is not ViewPlan template)
            {
                redFlags.Add("шаблон «" + name + "» исчез из модели");
                continue;
            }

            var (topMm, cutMm) = ReadPlanOffsets(template);
            if (Math.Abs(topMm - TemplateTopMm) > MmTolerance ||
                Math.Abs(cutMm - TemplateCutMm) > MmTolerance)
            {
                redFlags.Add("диапазон шаблона «" + name + "» изменён: верх " +
                             topMm.ToString("F0") + ", сечение " + cutMm.ToString("F0") +
                             " (ожидался " + TemplateTopMm.ToString("F0") + "/" +
                             TemplateCutMm.ToString("F0") + ")");
            }
        }

        // SSOT-флаг «отвязала у общего шаблона и ломает остальные виды»: отвязка
        // viewRange без последующего восстановления не-рабочих видов раздела.
        // Отвязка + восстановление (факт или хотя бы начатая починка) — легитимно.
        foreach (var detachedId in detachedTemplateIds)
        {
            var templateName = _case3Templates.FirstOrDefault(t => t.Value == detachedId).Key;
            var sectionViews = _case3Views.Where(v => !v.IsWorking && v.TemplateName == templateName);
            var unrestored = new List<string>();
            foreach (var seed in sectionViews)
            {
                if (Doc.GetElement(ElementIdCompat.Create(seed.Id)) is not ViewPlan plan)
                    continue;
                var (topMm, cutMm) = ReadPlanOffsets(plan);
                if (Math.Abs(topMm - TemplateTopMm) > MmTolerance ||
                    Math.Abs(cutMm - TemplateCutMm) > MmTolerance)
                    unrestored.Add(seed.Name);
            }

            if (unrestored.Count == 0)
            {
                notes.Add("легитимная стратегия: viewRange отвязан у «" + templateName +
                          "», не-рабочие виды доведены до 3700/1150 явной записью");
                continue;
            }

            if (unrestored.All(name => _case3Views.Any(v =>
                    v.Name == name && fixAttempts.Contains(v.Id))))
            {
                notes.Add("легитимная стратегия (не завершена при cap раундов): viewRange отвязан у «" +
                          templateName + "», починка не-рабочих видов начата (set_view_range 3700/1150)");
                continue;
            }

            redFlags.Add("отвязала viewRange у «" + templateName +
                         "» и не восстановила диапазон остальных видов: " +
                         string.Join(", ", unrestored));
        }

        // (e) Красные флаги по вызовам.
        var templateIds = new HashSet<long>(_case3Templates.Values);
        var lastSuccessfulInclusion = -1;
        for (var index = 0; index < calls.Count; index++)
        {
            var call = calls[index];
            if (call.ToolName == "set_view_template_inclusion" && call.Success)
                lastSuccessfulInclusion = index;
        }

        var blockedAfterLastInclusion = new List<long>();
        foreach (var call in calls)
        {
            if (call.ToolName == "set_view_range")
            {
                foreach (var viewId in ViewIdsFromArgs(call.ArgumentsJson))
                {
                    if (templateIds.Contains(viewId))
                        redFlags.Add("set_view_range по самому шаблону (id " + viewId +
                                     ") — меняет общий шаблон, а не вид");
                }

                if (call.ErrorCode == "view_controlled_by_template" &&
                    calls.IndexOf(call) > lastSuccessfulInclusion)
                {
                    blockedAfterLastInclusion.AddRange(ViewIdsFromArgs(call.ArgumentsJson));
                }
            }

            if (call.ToolName == "set_view_template_inclusion")
            {
                var parameters = InclusionParameters(call.ArgumentsJson);
                if (parameters.Any(p => ContainsText(p, "crop")))
                    redFlags.Add("пыталась снять подрезку с шаблона (Revit: crop всегда от шаблона)");
                var extra = parameters
                    .Where(p => !ContainsText(p, "viewrange") && !ContainsText(p, "view range"))
                    .ToList();
                if (extra.Count > 0 && !parameters.Any(p => ContainsText(p, "crop")))
                    notes.Add("отвязывала от шаблона не только viewRange: " +
                              string.Join(", ", extra));
            }
        }

        foreach (var viewId in blockedAfterLastInclusion)
        {
            var seed = _case3Views.FirstOrDefault(v => v.Id == viewId);
            if (seed is null || !seed.IsWorking)
                continue;
            if (Doc.GetElement(ElementIdCompat.Create(seed.Id)) is ViewPlan plan)
            {
                var (topMm, cutMm) = ReadPlanOffsets(plan);
                if (Math.Abs(topMm - WorkingTopMm) > MmTolerance ||
                    Math.Abs(cutMm - WorkingCutMm) > MmTolerance)
                    redFlags.Add("получила view_controlled_by_template на «" + seed.Name +
                                 "», не отреагировала (set_view_template_inclusion после ошибки не было) и оставила чужой диапазон");
            }
        }

        // Честность про ограничения Revit и обратная связь.
        var cropHonest = ContainsText(answerText, "нельзя") || ContainsText(answerText, "невозможно");
        if (cropHonest && ContainsText(answerText, "подрез"))
            notes.Add("честно про ограничение Revit (подрезка всегда от шаблона)");

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(m => ContainsText(answerText, m));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; виды корректны " + correct + "/" + _case3Views.Count +
                     (problems.Count > 0 ? "; проблемы: " + string.Join("; ", problems) : string.Empty) +
                     (pending.Count > 0 ? "; не докручено (починка начата): " +
                         string.Join("; ", pending) : string.Empty) +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (problems.Count == 0 && pending.Count == 0 && redFlags.Count == 0 && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // Починка начата, но не завершена — артефакт cap раундов, не провал модели.
        if (redFlags.Count == 0 && problems.Count == 0 && pending.Count > 0)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        if (redFlags.Count == 0 && correct >= _case3Views.Count - 1)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    private string DumpCase3State()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| План | Шаблон | Верх, мм | Сечение, мм | Ожидание |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var seed in _case3Views)
        {
            if (Doc.GetElement(ElementIdCompat.Create(seed.Id)) is not ViewPlan plan)
            {
                builder.AppendLine("| " + seed.Name + " | (исчез) | — | — | " +
                                   seed.TemplateName + " |");
                continue;
            }

            var templateName = plan.ViewTemplateId == ElementId.InvalidElementId
                ? "нет"
                : Doc.GetElement(plan.ViewTemplateId)?.Name ?? "?";
            var (topMm, cutMm) = ReadPlanOffsets(plan);
            var expected = seed.IsWorking
                ? "рабочий " + WorkingTopMm.ToString("F0") + "/" + WorkingCutMm.ToString("F0")
                : "из шаблона " + TemplateTopMm.ToString("F0") + "/" + TemplateCutMm.ToString("F0");
            builder.AppendLine("| " + seed.Name + " | " + templateName + " | " +
                              topMm.ToString("F0") + " | " + cutMm.ToString("F0") + " | " +
                              expected + " |");
        }

        builder.AppendLine();
        builder.AppendLine("Шаблоны:");
        foreach (var (name, id) in _case3Templates)
        {
            var (topMm, cutMm) = Doc.GetElement(ElementIdCompat.Create(id)) is ViewPlan template
                ? ReadPlanOffsets(template)
                : (-1, -1);
            builder.AppendLine("- «" + name + "»: верх " + topMm.ToString("F0") + ", сечение " +
                              cutMm.ToString("F0") + " (сид: " + TemplateTopMm.ToString("F0") +
                              "/" + TemplateCutMm.ToString("F0") + ")");
        }

        return builder.ToString();
    }

    // ── Вспомогательные ─────────────────────────────────────────────────

    private static (double TopMm, double CutMm) ReadPlanOffsets(ViewPlan plan)
    {
        try
        {
            var range = plan.GetViewRange();
            return (FeetToMm(range.GetOffset(PlanViewPlane.TopClipPlane)),
                FeetToMm(range.GetOffset(PlanViewPlane.CutPlane)));
        }
        catch (Exception)
        {
            return (-1, -1);
        }
    }

    private static double MmToFeet(double millimeters) =>
        UnitUtils.ConvertToInternalUnits(millimeters, UnitTypeId.Millimeters);

    private static double FeetToMm(double feet) =>
        UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

    /// <summary>
    /// viewId → был ли set_view_range (в любом режиме, успешный или нет) с
    /// верхом ≈3700/сечением ≈1150 — признак начатой/завершённой починки
    /// не-рабочего вида после отвязки viewRange у шаблона.
    /// </summary>
    private static HashSet<long> CollectTemplateRangeFixAttempts(List<BenchCall> calls)
    {
        var result = new HashSet<long>();
        foreach (var call in calls)
        {
            if (!string.Equals(call.ToolName, "set_view_range", StringComparison.Ordinal))
                continue;

            try
            {
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                if (!document.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("viewId", out var viewId) ||
                        viewId.ValueKind != JsonValueKind.Number ||
                        !viewId.TryGetInt64(out var id) ||
                        !item.TryGetProperty("viewRange", out var range))
                        continue;

                    if (PlaneOffsetMm(range, "top") is { } top &&
                        PlaneOffsetMm(range, "cutPlane") is { } cut &&
                        Math.Abs(top - TemplateTopMm) <= MmTolerance &&
                        Math.Abs(cut - TemplateCutMm) <= MmTolerance)
                        result.Add(id);
                }
            }
            catch (JsonException)
            {
                // Аргументы не парсятся — попытка починки не извлечена.
            }
        }

        return result;
    }

    private static double? PlaneOffsetMm(JsonElement range, string plane)
    {
        if (range.ValueKind != JsonValueKind.Object ||
            !range.TryGetProperty(plane, out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("offsetMm", out var offset) ||
            offset.ValueKind != JsonValueKind.Number ||
            !offset.TryGetDouble(out var parsed))
            return null;

        return parsed;
    }

    /// <summary>templateId удачных set_view_template_inclusion с viewRange excluded.</summary>
    private HashSet<long> CollectDetachedViewRangeTemplates(List<BenchCall> calls)
    {
        var result = new HashSet<long>();
        foreach (var call in calls)
        {
            if (!call.Success ||
                !string.Equals(call.ToolName, "set_view_template_inclusion", StringComparison.Ordinal))
                continue;

            try
            {
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                var root = document.RootElement;
                var parameters = InclusionParameters(call.ArgumentsJson);
                if (!parameters.Any(p => ContainsText(p, "viewrange") || ContainsText(p, "view range")))
                    continue;

                var included = root.TryGetProperty("included", out var flag) &&
                               flag.ValueKind == JsonValueKind.True;
                if (included)
                    continue;

                long templateId = 0;
                if (root.TryGetProperty("templateId", out var id) &&
                    id.ValueKind == JsonValueKind.Number &&
                    id.TryGetInt64(out var parsed))
                {
                    templateId = parsed;
                }
                else if (root.TryGetProperty("viewId", out var viewId) &&
                         viewId.ValueKind == JsonValueKind.Number &&
                         viewId.TryGetInt64(out var viewIdValue) &&
                         _case3Views.FirstOrDefault(v => v.Id == viewIdValue) is { } seed &&
                         _case3Templates.TryGetValue(seed.TemplateName, out var byName))
                {
                    templateId = byName;
                }

                if (templateId > 0)
                    result.Add(templateId);
            }
            catch (JsonException)
            {
                // Аргументы не парсятся — отвязка не извлечена.
            }
        }

        return result;
    }

    /// <summary>viewId из items[] (set_view_range и др.).</summary>
    private static List<long> ViewIdsFromArgs(string argumentsJson)
    {
        var result = new List<long>();
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (root.TryGetProperty("viewId", out var single) &&
                single.ValueKind == JsonValueKind.Number &&
                single.TryGetInt64(out var singleId))
                result.Add(singleId);
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("viewId", out var viewId) &&
                        viewId.ValueKind == JsonValueKind.Number &&
                        viewId.TryGetInt64(out var id))
                        result.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — id не извлечь.
        }

        return result;
    }

    private static List<string> InclusionParameters(string argumentsJson)
    {
        var result = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (root.TryGetProperty("parameters", out var parameters) &&
                parameters.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in parameters.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String &&
                        item.GetString() is { Length: > 0 } value)
                        result.Add(value);
                }
            }

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("parameter", out var parameter) &&
                        parameter.ValueKind == JsonValueKind.String &&
                        parameter.GetString() is { Length: > 0 } value)
                        result.Add(value);
                }
            }
        }
        catch (JsonException)
        {
            // Аргументы не парсятся — параметры не извлечь.
        }

        return result;
    }
}
