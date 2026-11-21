using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Live dogfooding of the sheet write pack (G.5): a real LLM drives the sheet
/// tools on a seeded project and the transcript is written to
/// %TEMP%\smartcon-sheet-dogfood\transcript.md. Sheet creation, viewport
/// placement, renumber/rename + undo, batch creation and harness feedback are
/// covered. Verdicts are soft: a failed task is recorded in the transcript as
/// TASK FAILED and the run continues; only 2 of 3 operational tasks must pass.
/// Skipped unless SMARTCON_AGENT_LIVE=1 and an API key are configured.
/// </summary>
public sealed class AgentSheetDogfoodTests : RevitApiTest
{
    private const int MaxRoundsPerTask = 4;
    private const int OperationalTasks = 3;

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;

    private Document Doc => _document!;

    [Test]
    public async Task LiveDogfood_SheetWritePack()
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
        transcript.AppendLine("# SmartCon Agent — sheet dogfood (G.5)");
        transcript.AppendLine();
        transcript.AppendLine("- Provider: " + config.Provider);
        transcript.AppendLine("- Model: " + config.Model);
        transcript.AppendLine("- Revit: " + Doc.Application.VersionNumber);
        transcript.AppendLine("- Started: " + DateTime.Now.ToString("O"));
        transcript.AppendLine("- Seeded views:");
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
            transcript.AppendLine(DumpSheets());
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
        transcript.AppendLine("- Feedback answer: " + (string.IsNullOrWhiteSpace(feedbackAnswer) ? "no" : "yes"));
        transcript.AppendLine("- LLM requests: " + requests);
        transcript.AppendLine("- Tokens: prompt " + promptTokens + ", completion " +
                               completionTokens);
        transcript.AppendLine("- Finished: " + DateTime.Now.ToString("O"));

        var directory = Path.Combine(Path.GetTempPath(), "smartcon-sheet-dogfood");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "transcript.md");
        File.WriteAllText(path, transcript.ToString(), Encoding.UTF8);
        Console.WriteLine("Sheet dogfood transcript: " + path);

        await Assert.That(passed).IsGreaterThanOrEqualTo(2);
    }

    [After(Test)]
    public void Close()
    {
        _document?.Close(false);
    }

    private void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        var titleBlockAvailable = false;

        _transactions.RunInTransaction(Doc, "Seed sheet dogfood model", doc =>
        {
            var levelIds = new List<ElementId>();
            for (var index = 1; index <= 3; index++)
            {
                var level = ModelSeed.CreateLevel(doc, (index - 1) * 10.0);
                level.Name = "SC_Уровень " + index.ToString(CultureInfo.InvariantCulture);
                levelIds.Add(level.Id);
            }

            for (var index = 1; index <= 3; index++)
            {
                ModelSeed.CreateViewPlanChecked(
                    doc,
                    levelIds[index - 1],
                    "SC_План " + index.ToString(CultureInfo.InvariantCulture) + " этажа");
            }

            // Viewport.Create returns null on a document that was never
            // regenerated with model geometry (IT-verified on Revit 2025);
            // each plan needs wall geometry on ITS level — a wall on level 1
            // alone leaves the level-2/3 plans unplaceable (live dogfood
            // G.5: create_viewports failed with view_not_placeable).
            for (var index = 0; index < levelIds.Count; index++)
                ModelSeed.TryCreateWallAt(doc, levelIds[index], index * 5.0);

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

            titleBlockAvailable = titleBlockId is not null;
        });

        if (!titleBlockAvailable)
            Skip.Test("В шаблоне проекта нет Title Block и библиотека Revit его не отдала — листы не создать");
    }

    private List<string> SeedSummary()
    {
        var lines = new List<string>();
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewPlan)))
        {
            var plan = (ViewPlan)element;
            if (!plan.Name.StartsWith("SC_", StringComparison.Ordinal))
                continue;
            lines.Add(plan.Name + " (id " + plan.Id.GetValue() +
                      ", уровень " + (plan.GenLevel?.Name ?? "?") + ")");
        }

        return lines;
    }

    private List<IAgentTool> CreateTools()
    {
        var context = new StubRevitContext(Doc);
        return new List<IAgentTool>
        {
            new ListViewsTool(context),
            new ListSheetsTool(context),
            new GetSheetContentTool(context),
            new CreateSheetTool(context, _transactions!, _changeSets!),
            new CreateViewportsTool(context, _transactions!, _changeSets!),
            new MoveViewportTool(context, _transactions!, _changeSets!),
            new SetSheetNumberTool(context, _transactions!, _changeSets!),
            new RenameSheetTool(context, _transactions!, _changeSets!),
            new SanitizeSheetNumbersTool(context, _transactions!, _changeSets!),
            new UndoLastTool(context, _transactions!, _changeSets!)
        };
    }

    private static List<string> BuildTasks() => new()
    {
        "Выпускаем раздел ОВ. Создай лист с номером ОВ-01 и именем „План 1 этажа“ " +
        "и размести на нём вид SC_План 1 этажа примерно в центре листа.",

        "Лист ОВ-01 нужно перенумеровать в ОВ-02 и переименовать в „План 1 этажа (копия)“. " +
        "Затем верни всё как было одной операцией отката.",

        "Создай листы с номерами ОВ-03 … ОВ-05 (три штуки, имена — План 1/2/3 этажа) " +
        "и разложи на каждом соответствующий вид.",

        "Оцени по 5-балльной шкале, насколько удобно тебе работать с этими инструментами " +
        "для оформления листов: что удобно, что неудобно, чего не хватило? Ответь кратко списком."
    };

    private string ExecuteTool(
        DogfoodToolCall call,
        IReadOnlyDictionary<string, IAgentTool> tools)
    {
        if (!tools.TryGetValue(call.Name, out var tool))
        {
            return "{\"success\":false,\"errorCode\":\"unknown_tool\",\"message\":\"Unknown tool " +
                   call.Name + "\"}";
        }

        JsonElement args;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return "{\"success\":false,\"errorCode\":\"invalid_arguments\",\"message\":\"" +
                   ex.Message.Replace("\"", "'") + "\"}";
        }

        var result = tool switch
        {
            AgentWriteToolBase write => write.ExecuteForTests(args, approved: true, CancellationToken.None),
            AgentReadToolBase read => read.ExecuteForTests(Doc, args, CancellationToken.None),
            _ => null
        };

        return result is null
            ? "{\"success\":false,\"errorCode\":\"unknown_tool\",\"message\":\"Tool cannot be executed\"}"
            : ToolResultBudget.Serialize(result, 8000);
    }

    private TaskVerdict EvaluateTask(
        int taskIndex,
        IReadOnlyList<DogfoodCall> calls,
        IReadOnlyList<int> callsPerRound)
    {
        var stats = TaskStats.From(calls, callsPerRound);
        var detail = stats.Describe();

        switch (taskIndex)
        {
            case 0:
            {
                var sheet = FindSheet("ОВ-01");
                if (sheet is null)
                    return new TaskVerdict(false, detail + "; лист ОВ-01 не создан");
                if (!string.Equals(sheet.Name, "План 1 этажа", StringComparison.Ordinal))
                    return new TaskVerdict(false,
                        detail + "; имя листа «" + sheet.Name + "» ≠ «План 1 этажа»");
                var viewName = ViewportViewName(sheet, "SC_План 1 этажа");
                if (viewName is null)
                    return new TaskVerdict(false,
                        detail + "; вид SC_План 1 этажа не размещён на листе");
                return new TaskVerdict(true,
                    detail + "; ОВ-01 «" + sheet.Name + "» с видом " + viewName);
            }
            case 1:
            {
                var renumbered = calls.Any(call =>
                    call.Success && call.ToolName == "set_sheet_number" &&
                    ArgsContains(call, "ОВ-02"));
                var renamed = calls.Any(call =>
                    call.Success && call.ToolName == "rename_sheet" &&
                    ArgsContains(call, "копия"));
                var undoCount = calls.Count(call =>
                    call.Success && call.ToolName == "undo_last");

                if (!renumbered)
                    return new TaskVerdict(false, detail + "; не было успешного set_sheet_number → ОВ-02");
                if (!renamed)
                    return new TaskVerdict(false, detail + "; не было успешного rename_sheet → «(копия)»");
                if (undoCount == 0)
                    return new TaskVerdict(false, detail + "; не было успешного undo_last");

                var restored = FindSheet("ОВ-01");
                var renamedAway = FindSheet("ОВ-02");
                if (restored is null || renamedAway is not null ||
                    !string.Equals(restored.Name, "План 1 этажа", StringComparison.Ordinal))
                {
                    return new TaskVerdict(false,
                        detail + "; после отката состояние не restored: " +
                        "ОВ-01=" + (restored?.Name ?? "(нет)") + ", ОВ-02=" + (renamedAway?.Name ?? "(нет)"));
                }

                return new TaskVerdict(true,
                    detail + "; ОВ-02 сделан и откачен (undo_last ×" + undoCount +
                    "), лист снова ОВ-01 «" + restored.Name + "»");
            }
            case 2:
            {
                var problems = new List<string>();
                for (var floor = 1; floor <= 3; floor++)
                {
                    var number = "ОВ-0" + (floor + 2);
                    var expectedName = "План " +
                                       floor.ToString(CultureInfo.InvariantCulture) + " этажа";
                    var expectedView = "SC_План " +
                                       floor.ToString(CultureInfo.InvariantCulture) + " этажа";

                    var sheet = FindSheet(number);
                    if (sheet is null)
                    {
                        problems.Add(number + " не создан");
                        continue;
                    }

                    if (!string.Equals(sheet.Name, expectedName, StringComparison.Ordinal))
                        problems.Add(number + " имя «" + sheet.Name + "» ≠ «" + expectedName + "»");

                    if (ViewportViewName(sheet, expectedView) is null)
                        problems.Add(number + " без вида " + expectedView);
                }

                return problems.Count == 0
                    ? new TaskVerdict(true, detail + "; листы ОВ-03…ОВ-05 с видами созданы")
                    : new TaskVerdict(false, detail + "; " + string.Join("; ", problems));
            }
            default:
                return new TaskVerdict(false, "неизвестная задача " + taskIndex);
        }
    }

    private ViewSheet? FindSheet(string number)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)))
        {
            if (element is ViewSheet sheet &&
                string.Equals(sheet.SheetNumber, number, StringComparison.Ordinal))
                return sheet;
        }

        return null;
    }

    private string? ViewportViewName(ViewSheet sheet, string viewName)
    {
        foreach (var viewportId in sheet.GetAllViewports())
        {
            if (Doc.GetElement(viewportId) is not Viewport viewport)
                continue;
            if (Doc.GetElement(viewport.ViewId) is View view &&
                string.Equals(view.Name, viewName, StringComparison.Ordinal))
                return view.Name;
        }

        return null;
    }

    private string DumpSheets()
    {
        var builder = new StringBuilder();
        var sheets = new FilteredElementCollector(Doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .OrderBy(sheet => sheet.SheetNumber, StringComparer.Ordinal)
            .ToList();
        if (sheets.Count == 0)
            return builder.AppendLine("(листов нет)").ToString();

        foreach (var sheet in sheets)
        {
            builder.AppendLine(
                "- " + sheet.SheetNumber + " «" + sheet.Name + "» (id " + sheet.Id.GetValue() + ")");
            foreach (var viewportId in sheet.GetAllViewports())
            {
                if (Doc.GetElement(viewportId) is not Viewport viewport)
                    continue;
                var view = Doc.GetElement(viewport.ViewId);
                var center = viewport.GetBoxCenter();
                builder.AppendLine(
                    "    - viewport id " + viewport.Id.GetValue() + " → " + (view?.Name ?? "?") +
                    " @ (" + Mm(center.X) + "; " + Mm(center.Y) + ") мм");
            }
        }

        return builder.ToString();
    }

    private static string Mm(double internalUnits) =>
        UnitUtils.ConvertFromInternalUnits(internalUnits, UnitTypeId.Millimeters)
            .ToString("F0", CultureInfo.InvariantCulture);

    private static bool ArgsContains(DogfoodCall call, string substring) =>
        call.ArgumentsJson.Contains(substring, StringComparison.Ordinal);

    private static string SystemPrompt() =>
        "Ты SmartCon Agent внутри Autodesk Revit и работаешь с листами (ViewSheet) и видами " +
        "через инструменты пакета sheets. Отвечай по-русски. Сначала читай проект " +
        "(list_views, list_sheets, get_sheet_content), не выдумывай id — бери их из результатов. " +
        "Координаты вьюпортов задаются в миллиметрах бумаги (pointMmX/pointMmY от левого нижнего " +
        "угла листа). Операции записи по умолчанию идут с dryRun=true (проверка без изменений); " +
        "когда пользователь прямо просит создать/перенумеровать/переименовать/разместить — " +
        "выполняй apply (dryRun=false) и подтверждай результат чтением. Откат последней " +
        "применённой операции — undo_last (откатывает одну операцию; чтобы вернуть несколько, " +
        "вызывай его последовательно). Несколько независимых вызовов можно объединять в один " +
        "ответ. Если инструмент вернул ошибку, объясни её пользователю и не повторяй тот же " +
        "вызов без изменений. Отвечай кратко: что сделано и ключевые числа.";

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";

    private sealed record TaskVerdict(bool Passed, string Detail);

    private sealed record DogfoodCall(
        string ToolName,
        string ArgumentsJson,
        string ResultJson,
        bool Success,
        string? ErrorCode,
        int Round)
    {
        public static DogfoodCall From(DogfoodToolCall call, string resultJson, int round)
        {
            var success = false;
            string? errorCode = null;
            try
            {
                using var document = JsonDocument.Parse(resultJson);
                var root = document.RootElement;
                if (root.TryGetProperty("success", out var successValue) &&
                    successValue.ValueKind == JsonValueKind.True)
                {
                    success = true;
                }

                if (root.TryGetProperty("errorCode", out var codeValue) &&
                    codeValue.ValueKind == JsonValueKind.String)
                {
                    errorCode = codeValue.GetString();
                }
            }
            catch (JsonException)
            {
                // A malformed result is reported as a failure without a code.
            }

            return new DogfoodCall(
                call.Name, call.ArgumentsJson, resultJson, success, errorCode, round);
        }
    }

    private sealed class TaskStats
    {
        private TaskStats(
            string sequence, int rounds, int maxParallel, int applyCalls, int dryRunCalls)
        {
            Sequence = sequence;
            Rounds = rounds;
            MaxParallel = maxParallel;
            ApplyCalls = applyCalls;
            DryRunCalls = dryRunCalls;
        }

        public string Sequence { get; }

        public int Rounds { get; }

        public int MaxParallel { get; }

        public int ApplyCalls { get; }

        public int DryRunCalls { get; }

        public string Describe() =>
            "tools [" + Sequence + "], rounds " + Rounds +
            ", max parallel calls/round " + MaxParallel +
            ", apply(dryRun=false) " + ApplyCalls + ", dryRun=true " + DryRunCalls;

        public static TaskStats From(
            IReadOnlyList<DogfoodCall> calls,
            IReadOnlyList<int> callsPerRound)
        {
            var sequence = calls.Count == 0
                ? "(нет вызовов)"
                : string.Join(" → ", calls.Select(call =>
                    call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")")));

            var applyCalls = 0;
            var dryRunCalls = 0;
            foreach (var call in calls)
            {
                if (call.ToolName is not ("list_views" or "list_sheets" or "get_sheet_content"))
                {
                    if (IsApply(call))
                        applyCalls++;
                    else
                        dryRunCalls++;
                }
            }

            var maxParallel = callsPerRound.Count == 0 ? 0 : callsPerRound.Max();
            return new TaskStats(sequence, callsPerRound.Count, maxParallel, applyCalls, dryRunCalls);
        }

        private static bool IsApply(DogfoodCall call)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                return document.RootElement.TryGetProperty("dryRun", out var value)
                       && value.ValueKind == JsonValueKind.False;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
