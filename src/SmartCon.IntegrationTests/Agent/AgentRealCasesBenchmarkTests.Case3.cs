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
/// verdict distinguishes all three states.
/// Verdict — in the partial Case3.Evaluation (EvaluateCase3). Partial of
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
}
