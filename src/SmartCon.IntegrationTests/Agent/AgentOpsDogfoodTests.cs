using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Live dogfooding of the ops packs G.2–G.4 (schedules, project shared
/// parameters, family types): a real LLM drives the tools on a seeded project
/// and the transcript is written to %TEMP%\smartcon-ops-dogfood\transcript.md.
/// The shared parameter file is swapped to a temp one with the «ОВ_Секция»
/// definition seeded in advance. Verdicts are soft: only 2 of 3 operational
/// tasks must pass. Skipped unless SMARTCON_AGENT_LIVE=1 and an API key are
/// configured.
/// </summary>
public sealed partial class AgentOpsDogfoodTests : RevitApiTest
{
    private const int MaxRoundsPerTask = 4;
    private const int OperationalTasks = 3;
    private const string SheetNumber = "ОВ-01";
    private const string ParameterName = "ОВ_Секция";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private SharedFileScope? _sharedFile;
    private List<long> _seedWallIds = null!;
    private long _seedWallTypeId;
    private long _sheetId;
    private double _sheetWidthMm;
    private double _sheetHeightMm;
    private string _markField = "Mark";
    private string _commentsField = "Comments";
    private string _typeField = "Type";

    private Document Doc => _document!;

    [Test]
    public async Task LiveDogfood_OpsPacks()
    {
        var config = DogfoodConfig.TryLoad();
        if (config is null)
        {
            Skip.Test("SMARTCON_AGENT_LIVE != 1 или не настроен API-ключ");
            return;
        }

        SeedModel();
        var tools = CreateTools();
        var definitions = tools.Select(tool => tool.Definition).ToList();
        var byName = tools.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);

        var transcript = new StringBuilder();
        transcript.AppendLine("# SmartCon Agent — ops dogfood (G.2–G.4)");
        transcript.AppendLine();
        transcript.AppendLine("- Provider: " + config.Provider);
        transcript.AppendLine("- Model: " + config.Model);
        transcript.AppendLine("- Revit: " + Doc.Application.VersionNumber);
        transcript.AppendLine("- Started: " + DateTime.Now.ToString("O"));
        transcript.AppendLine("- Seeded model:");
        foreach (var line in SeedSummary())
            transcript.AppendLine("  - " + line);
        transcript.AppendLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);

        var tasks = BuildTasks();
        var passed = 0;
        var requests = 0;
        var promptTokens = 0;
        var completionTokens = 0;
        string? feedbackAnswer = null;

        for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
        {
            var task = tasks[taskIndex];
            transcript.AppendLine("## " + (taskIndex + 1) + ". " + task);
            transcript.AppendLine();

            var messages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "system", ["content"] = SystemPrompt() },
                new() { ["role"] = "user", ["content"] = task }
            };

            var calls = new List<DogfoodCall>();
            var callsPerRound = new List<int>();
            string? answer = null;

            for (var round = 1; round <= MaxRoundsPerTask; round++)
            {
                requests++;
                var response = DogfoodClient.CompleteAsync(
                        http, config, messages, definitions, CancellationToken.None)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                promptTokens += response.PromptTokens;
                completionTokens += response.CompletionTokens;

                if (response.Error is not null)
                {
                    transcript.AppendLine("**LLM error:** " + response.Error);
                    transcript.AppendLine();
                    break;
                }

                if (response.ToolCalls.Count == 0)
                {
                    answer = response.Content;
                    break;
                }

                callsPerRound.Add(response.ToolCalls.Count);

                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = response.Content,
                    ["tool_calls"] = response.ToolCalls.Select(call => new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.ArgumentsJson
                        }
                    }).ToList()
                });

                foreach (var call in response.ToolCalls)
                {
                    var resultJson = ExecuteTool(call, byName);
                    var record = DogfoodCall.From(call, resultJson, round);
                    calls.Add(record);

                    transcript.AppendLine(
                        "- r" + round + " `" + call.Name + "` " +
                        Shorten(call.ArgumentsJson, 240));
                    transcript.AppendLine(
                        "  → " + (record.Success ? "ok" : "FAIL " + record.ErrorCode) + " " +
                        Shorten(resultJson, 420));

                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.Id,
                        ["content"] = resultJson
                    });
                }
            }

            transcript.AppendLine();
            transcript.AppendLine("**Answer:** " + (answer ?? "(no final answer)"));

            if (taskIndex < OperationalTasks)
            {
                var verdict = EvaluateTask(taskIndex, calls, callsPerRound);
                if (verdict.Passed)
                    passed++;

                transcript.AppendLine(
                    "**Verdict:** " + (verdict.Passed ? "PASS" : "FAIL — TASK FAILED") +
                    " — " + verdict.Detail);
            }
            else
            {
                feedbackAnswer = answer;
                transcript.AppendLine(
                    string.IsNullOrWhiteSpace(answer)
                        ? "**Verdict:** FAIL — TASK FAILED (нет ответа на вопрос обратной связи)"
                        : "**Verdict:** FEEDBACK CAPTURED");
            }

            transcript.AppendLine();
            transcript.AppendLine("### State read-back");
            transcript.AppendLine(DumpState());
            transcript.AppendLine();
        }

        transcript.AppendLine("## Harness feedback");
        transcript.AppendLine();
        transcript.AppendLine(feedbackAnswer ?? "(модель не ответила на вопрос обратной связи)");
        transcript.AppendLine();

        transcript.AppendLine("## Summary");
        transcript.AppendLine();
        transcript.AppendLine("- Operational tasks: " + OperationalTasks);
        transcript.AppendLine("- Passed: " + passed);
        transcript.AppendLine("- Feedback answer: " +
                              (string.IsNullOrWhiteSpace(feedbackAnswer) ? "no" : "yes"));
        transcript.AppendLine("- LLM requests: " + requests);
        transcript.AppendLine("- Tokens: prompt " + promptTokens + ", completion " +
                               completionTokens);
        transcript.AppendLine("- Finished: " + DateTime.Now.ToString("O"));

        var directory = Path.Combine(Path.GetTempPath(), "smartcon-ops-dogfood");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "transcript.md");
        File.WriteAllText(path, transcript.ToString(), Encoding.UTF8);
        Console.WriteLine("Ops dogfood transcript: " + path);

        await Assert.That(passed).IsGreaterThanOrEqualTo(2);
        await Assert.That(string.IsNullOrWhiteSpace(feedbackAnswer)).IsFalse();
    }

    [After(Test)]
    public void Close()
    {
        _sharedFile?.Dispose();
        _document?.Close(false);
    }

    private void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");
        if (!ModelSeed.HasWallType(Doc))
            Skip.Test("В шаблоне проекта нет типов стен");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _sharedFile = SharedFileScope.Create(Application);
        SeedSharedDefinition(ParameterName);

        var viewportCreated = false;
        _transactions.RunInTransaction(Doc, "Seed ops dogfood model", doc =>
        {
            var levelIds = new List<ElementId>();
            for (var index = 1; index <= 2; index++)
            {
                var level = ModelSeed.CreateLevel(doc, (index - 1) * 10.0);
                level.Name = "SC_Уровень " + index.ToString(CultureInfo.InvariantCulture);
                levelIds.Add(level.Id);
            }

            var planIds = new List<ElementId>();
            for (var index = 1; index <= 2; index++)
            {
                var plan = ModelSeed.CreateViewPlanChecked(
                    doc,
                    levelIds[index - 1],
                    "SC_План " + index.ToString(CultureInfo.InvariantCulture));
                planIds.Add(plan.Id);
            }

            // Viewport.Create returns null on a document that was never
            // regenerated with model geometry — each plan needs a wall on ITS
            // level (live dogfood G.5: view_not_placeable).
            _seedWallIds = new List<long>();
            _seedWallTypeId = FirstWallTypeId(doc);
            for (var index = 0; index < levelIds.Count; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, levelIds[index], index * 5.0);
                if (wall is not null)
                    _seedWallIds.Add(wall.Id.GetValue());
            }

            ProbeScheduleFieldNames(doc);

            var titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
            if (titleBlockId is null)
            {
                var titleBlockPath = ModelSeed.FindTitleBlockFamilyFile(
                    doc.Application.VersionNumber);
                if (titleBlockPath is not null)
                {
                    doc.LoadFamily(titleBlockPath, out _);
                    titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
                }
            }

            if (titleBlockId is null)
                return;

            var sheet = ViewSheet.Create(doc, titleBlockId);
            sheet.SheetNumber = SheetNumber;
            sheet.Name = "План 1 этажа";
            _sheetId = sheet.Id.GetValue();

            doc.Regenerate();
            ReadSheetSize(sheet);
            var viewport = Viewport.Create(doc, sheet.Id, planIds[0], new XYZ(0.2, 0.15, 0));
            viewportCreated = viewport is not null;
        });

        if (_seedWallIds.Count == 0)
            Skip.Test("Не удалось создать стены в сиде — геометрия для видов не прогрета");
        if (!viewportCreated)
            Skip.Test("Viewport.Create вернул null — вид SC_План 1 не размещён на листе");
    }

    private void SeedSharedDefinition(string name)
    {
        var file = Application.OpenSharedParameterFile();
        var group = file!.Groups.get_Item("SmartCon") ?? file.Groups.Create("SmartCon");
        group!.Definitions.Create(new ExternalDefinitionCreationOptions(
            name, SpecTypeId.String.Text));
    }

    private void ProbeScheduleFieldNames(Document doc)
    {
        var probe = ModelSeed.CreateScheduleChecked(doc);
        foreach (var field in probe.Definition.GetSchedulableFields())
        {
            var name = field.GetName(doc);
            // A BuiltInParameter maps to several schedulable fields (e.g. the
            // wall schedule exposes both "Comments" and "Analytical: Comments");
            // the plain display name wins over the analytical duplicate,
            // otherwise the verdict compares against a field the model cannot add.
            if (field.ParameterId == new ElementId(BuiltInParameter.ALL_MODEL_MARK) &&
                !name.Contains("Analytical", StringComparison.Ordinal))
                _markField = name;
            if (field.ParameterId == new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) &&
                !name.Contains("Analytical", StringComparison.Ordinal))
                _commentsField = name;
            if (string.Equals(name, "Тип", StringComparison.Ordinal) ||
                string.Equals(name, "Type", StringComparison.Ordinal))
                _typeField = name;
        }

        doc.Delete(probe.Id);
    }

    private void ReadSheetSize(ViewSheet sheet)
    {
        try
        {
            var outline = sheet.Outline;
            _sheetWidthMm = UnitUtils.ConvertFromInternalUnits(
                outline.Max.U - outline.Min.U, UnitTypeId.Millimeters);
            _sheetHeightMm = UnitUtils.ConvertFromInternalUnits(
                outline.Max.V - outline.Min.V, UnitTypeId.Millimeters);
        }
        catch (Exception)
        {
            // Zero outline on empty sheets keeps 0 — evaluation will not check position.
        }
    }

    private static long FirstWallTypeId(Document doc)
    {
        var id = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType))
            .WhereElementIsElementType()
            .FirstElementId();
        return id.GetValue();
    }

    private List<string> SeedSummary()
    {
        var lines = new List<string>();
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewPlan)))
        {
            if (element is ViewPlan plan && plan.Name.StartsWith("SC_", StringComparison.Ordinal))
                lines.Add("plan " + plan.Name + " (id " + plan.Id.GetValue() +
                          ", уровень " + (plan.GenLevel?.Name ?? "?") + ")");
        }

        lines.Add("sheet " + SheetNumber + " (id " + _sheetId + ", " +
                  _sheetWidthMm.ToString("F0", CultureInfo.InvariantCulture) + "×" +
                  _sheetHeightMm.ToString("F0", CultureInfo.InvariantCulture) +
                  " мм) с видом SC_План 1");
        lines.Add("wallType id " + _seedWallTypeId + " «" +
                  Doc.GetElement(ElementIdCompat.Create(_seedWallTypeId))?.Name +
                  "», стены: " + string.Join(", ", _seedWallIds));
        lines.Add("schedule field names: тип=" + _typeField + ", марка=" + _markField +
                  ", комментарии=" + _commentsField);
        lines.Add("ФОП подменён на временный файл, определение «" + ParameterName +
                  "» уже создано в группе SmartCon");
        return lines;
    }

    private List<IAgentTool> CreateTools()
    {
        var context = new StubRevitContext(Doc);
        return new List<IAgentTool>
        {
            new ListSchedulesTool(context),
            new CreateScheduleTool(context, _transactions!, _changeSets!),
            new AddScheduleFieldTool(context, _transactions!, _changeSets!),
            new SetScheduleFilterTool(context, _transactions!, _changeSets!),
            new SetScheduleSortTool(context, _transactions!, _changeSets!),
            new PlaceScheduleOnSheetTool(context, _transactions!, _changeSets!),
            new GetSheetContentTool(context),
            new ListProjectParametersTool(context),
            new AddProjectSharedParameterTool(context, _transactions!, _changeSets!),
            new ListFamiliesTool(context),
            new DuplicateFamilyTypeTool(context, _transactions!, _changeSets!),
            new ReplaceFamilyTypeTool(context, _transactions!, _changeSets!),
            new UndoLastTool(context, _transactions!, _changeSets!)
        };
    }

    private static List<string> BuildTasks() => new()
    {
        "Сделай спецификацию „Ведомость стен“ по категории стен: поля Тип, Марка, Комментарии; " +
        "скрой деталировку (не itemized — итог по типам); отсортируй по типу по возрастанию; " +
        "размести её на листе ОВ-01 в 50 мм от левого верхнего угла.",

        "Добавь в проект общий параметр „ОВ_Секция“ — текстовый, для экземпляров, " +
        "для категорий стен и воздуховодов. Затем покажи, что он появился в списке проектных параметров.",

        "Среди типов стен найди основной тип 200 мм (или любой тип стен, которых ≥2 вида). " +
        "Создай его копию с именем …_ОВ и переведи на неё все стены исходного типа одной операцией.",

        "Оцени по 5-балльной шкале удобство работы с инструментами спецификаций/параметров/типов: " +
        "что удобно, что неудобно, чего не хватило?"
    };

    private static string SystemPrompt() =>
        "Ты SmartCon Agent внутри Autodesk Revit и работаешь со спецификациями (ведомостями), " +
        "проектными параметрами и типами семейств через инструменты. Отвечай по-русски. " +
        "Сначала читай проект (list_schedules, get_sheet_content, list_project_parameters, " +
        "list_families), не выдумывай id — бери их из результатов. Имена полей спецификации — " +
        "локализованные отображаемые имена: если create_schedule или add_schedule_field вернул " +
        "field_not_available, возьми точное имя из availableFields в ответе и повтори. " +
        "place_schedule_on_sheet: pointMmX/pointMmY — левый ВЕРХНИЙ угол таблицы в миллиметрах " +
        "бумаги от левого НИЖНЕГО угла листа; размер листа (widthMm/heightMm) есть в ответе " +
        "get_sheet_content. Операции записи по умолчанию идут с dryRun=true (проверка без " +
        "изменений); когда пользователь прямо просит создать/добавить/изменить — выполняй apply " +
        "(dryRun=false) и подтверждай результат чтением. Откат последней применённой операции — " +
        "undo_last. Несколько независимых вызовов можно объединять в один ответ. Если инструмент " +
        "вернул ошибку, объясни её пользователю и не повторяй тот же вызов без изменений. " +
        "Отвечай кратко: что сделано и ключевые числа.";

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";
}
