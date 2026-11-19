using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
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
/// Live dogfooding of the family editing pack (H1d): a real LLM drives the nine
/// family tools on a seeded family and the transcript is written to
/// %TEMP%\smartcon-family-dogfood. Read tasks, dry-run writes, honest failure
/// codes and apply writes are all covered.
/// Skipped unless SMARTCON_AGENT_LIVE=1 and an API key are configured.
/// </summary>
public sealed class AgentFamilyDogfoodTests : RevitApiTest
{
    private const int MaxRoundsPerTask = 4;

    private Document? _document;
    private FamilySeed? _seed;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;

    private Document Doc => _document!;

    private FamilySeed Seed => _seed!;

    [Test]
    public async Task LiveDogfood_FamilyEditingPack()
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
        transcript.AppendLine("# SmartCon Agent — family editing dogfood");
        transcript.AppendLine();
        transcript.AppendLine("- Provider: " + config.Provider);
        transcript.AppendLine("- Model: " + config.Model);
        transcript.AppendLine("- Revit: " + Doc.Application.VersionNumber);
        transcript.AppendLine("- Family: " + Seed.FamilyName);
        transcript.AppendLine("- Family id: " + Seed.FamilyId);
        transcript.AppendLine("- Started: " + DateTime.Now.ToString("O"));
        transcript.AppendLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);

        var tasks = BuildTasks();
        var passed = 0;
        var firstTry = 0;
        var answered = 0;
        var requests = 0;
        var promptTokens = 0;
        var completionTokens = 0;

        for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
        {
            var task = tasks[taskIndex];
            transcript.AppendLine("## " + (taskIndex + 1) + ". " + task.Prompt);
            transcript.AppendLine();

            var messages = new List<Dictionary<string, object?>>
            {
                new() { ["role"] = "system", ["content"] = SystemPrompt() },
                new() { ["role"] = "user", ["content"] = task.Prompt }
            };

            var calls = new List<DogfoodCall>();
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

            var verdict = Evaluate(task, calls);
            var taskFirstTry = verdict.Passed && IsFirstTry(calls, task);
            if (verdict.Passed)
                passed++;
            if (taskFirstTry)
                firstTry++;
            if (!string.IsNullOrWhiteSpace(answer))
                answered++;

            transcript.AppendLine();
            transcript.AppendLine(
                "**Verdict:** " + (verdict.Passed ? "PASS" : "FAIL") +
                (taskFirstTry ? " (first-try)" : string.Empty) + " — " + verdict.Detail);
            transcript.AppendLine("**Answer:** " + (answer ?? "(no final answer)"));
            transcript.AppendLine();
        }

        var directory = Path.Combine(Path.GetTempPath(), "smartcon-family-dogfood");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");

        var firstTryRate = tasks.Count == 0 ? 0 : (double)firstTry / tasks.Count;
        transcript.AppendLine("## Summary");
        transcript.AppendLine();
        transcript.AppendLine("- Tasks: " + tasks.Count);
        transcript.AppendLine("- Passed: " + passed);
        transcript.AppendLine("- First-try: " + firstTry + " (" +
                               (firstTryRate * 100).ToString("F1") + " %)");
        transcript.AppendLine("- Answered: " + answered);
        transcript.AppendLine("- LLM requests: " + requests);
        transcript.AppendLine("- Tokens: prompt " + promptTokens + ", completion " +
                               completionTokens);
        File.WriteAllText(path, transcript.ToString(), Encoding.UTF8);
        Console.WriteLine("Family dogfood transcript: " + path);

        await Assert.That(firstTryRate).IsGreaterThanOrEqualTo(0.90);
        await Assert.That(answered).IsGreaterThanOrEqualTo(tasks.Count - 3);
    }

    [After(Test)]
    public void Close()
    {
        _seed?.Dispose();
        _document?.Close(false);
    }

    private void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _seed = FamilySeed.Create(Application, Doc, "SC_AgentFamilyDogfood", out var reason);
        if (_seed is null)
            Skip.Test(reason ?? "Не удалось сидировать семейство");
    }

    private List<IAgentTool> CreateTools()
    {
        var context = new StubRevitContext(Doc);
        return new List<IAgentTool>
        {
            new ListFamiliesTool(context),
            new ListFamilyTypesTool(context),
            new GetFamilyParametersTool(context),
            new FindFamiliesWithParameterTool(context),
            new RenameFamilyParameterTool(context, _transactions!, _changeSets!),
            new AddFamilyParameterTool(context, _transactions!, _changeSets!),
            new SetFamilyParameterValuesTool(context, _transactions!, _changeSets!),
            new SetFamilyFormulaTool(context, _transactions!, _changeSets!),
            new MapFamilyParametersTool(context, _transactions!, _changeSets!)
        };
    }

    private List<FamilyTask> BuildTasks()
    {
        var family = Seed.FamilyName;
        return new List<FamilyTask>
        {
            new("Покажи все загруженные семейства проекта.", Names("list_families"), null, family),
            new("Сколько типоразмеров у семейства '" + family + "'? Перечисли их.",
                Names("list_family_types"), null, "Тип2"),
            new("Покажи параметры типа семейства '" + family + "' (только параметры типа).",
                Names("get_family_parameters"), null, FamilySeed.WidthParam),
            new("Какая формула у параметра " + FamilySeed.AreaParam + " в семействе '" + family + "'?",
                Names("get_family_parameters"), null, "Ширина * Высота"),
            new("Найди в проекте семейства, у которых есть параметр " + FamilySeed.WidthParam + ".",
                Names("find_families_with_parameter"), null, family),
            new("В каких семействах есть параметр " + FamilySeed.PressureParam + "? Покажи имя семейства.",
                Names("find_families_with_parameter"), null, family),
            new("Покажи параметры экземпляра у семейства '" + family + "'.",
                Names("get_family_parameters"), null, FamilySeed.InstanceParam),
            new("Какие значения параметра " + FamilySeed.CodeParam + " у типов семейства '" + family + "'?",
                Names("get_family_parameters"), null, "A-1"),
            new("Сколько всего параметров у семейства '" + family + "' и какие из них можно менять?",
                Names("get_family_parameters"), null, "canSetValue"),

            new("Добавь в семейство '" + family + "' текстовый параметр типа «Заказчик». " +
                "Только проверь, не сохраняй.",
                Names("add_family_parameter"), null, "Заказчик"),
            new("Переименуй параметр " + FamilySeed.CodeParam + " в Артикул в семействе '" + family +
                "', но пока не применяй.",
                Names("rename_family_parameter"), null, "Артикул"),
            new("Проверь установку: " + FamilySeed.WidthParam + " = 1200 мм для типа " + FamilySeed.Type1 +
                " в семействе '" + family + "'.",
                Names("set_family_parameter_values"), null, FamilySeed.Type1),
            new("Проверь формулу: параметру " + FamilySeed.SizeParam + " задать «" + FamilySeed.WidthParam +
                " + 100» в семействе '" + family + "'.",
                Names("set_family_formula"), null, "Ширина + 100"),
            new("Проверь перенос значений: " + FamilySeed.WidthParam + " → " + FamilySeed.HeightParam +
                " для всех типов в семействе '" + family + "'.",
                Names("map_family_parameters"), null, null),
            new("Попробуй установить " + FamilySeed.WidthParam + " = «широкая» для типа " +
                FamilySeed.Type1 + " в семействе '" + family + "' и покажи ошибку инструмента.",
                Names("set_family_parameter_values"),
                new[] { "unit_required", "invalid_value" }, null, FamilySeed.WidthParam),
            new("Попробуй добавить в семейство '" + family + "' ещё один параметр с точно " +
                "таким же именем «" + FamilySeed.CodeParam + "» (текст) и покажи ошибку.",
                Names("add_family_parameter"), new[] { "parameter_exists" }, null,
                FamilySeed.CodeParam),
            new("Задай параметру " + FamilySeed.WidthParam + " формулу " +
                "«НесуществующийПараметр * 2» в семействе '" + family + "' и покажи ошибку.",
                Names("set_family_formula"),
                new[] { "invalid_formula", "formula_not_allowed" }, null, "Несуществующий"),
            new("Задай параметру " + FamilySeed.WidthParam + " формулу «" + FamilySeed.AreaParam + " / " +
                FamilySeed.HeightParam + "» в семействе '" + family + "'.",
                Names("set_family_formula"),
                new[] { "invalid_formula", "formula_not_allowed" }, null),
            new("Попробуй установить значение параметра " + FamilySeed.AreaParam + " = 25 м2 для " +
                "типа " + FamilySeed.Type1 + " в семействе '" + family +
                "' и покажи ошибку инструмента.",
                Names("set_family_parameter_values"),
                new[] { "formula_not_allowed", "unit_required" }, null, FamilySeed.AreaParam),
            new("Покажи параметры семейства «НетТакого_Семейства_XZ».",
                Names("get_family_parameters"), new[] { "family_not_found" }, null),

            new("Добавь в семейство '" + family + "' текстовый параметр типа «Заказчик» и сохрани.",
                Names("add_family_parameter"), null, "changeSetId"),
            new("Установи " + FamilySeed.WidthParam + " = 1300 мм для типа " + FamilySeed.Type1 +
                " в семействе '" + family + "' и сохрани.",
                Names("set_family_parameter_values"), null, "changeSetId"),
            new("Переименуй " + FamilySeed.CodeParam + " в Артикул в семействе '" + family + "' и сохрани.",
                Names("rename_family_parameter"), null, "changeSetId"),
            new("Задай параметру " + FamilySeed.SizeParam + " формулу «" + FamilySeed.WidthParam +
                " - 100» в семействе '" + family + "' и сохрани.",
                Names("set_family_formula"), null, "changeSetId"),
            new("Перенеси значения " + FamilySeed.WidthParam + " → " + FamilySeed.HeightParam +
                " для всех типов семейства '" + family + "' и сохрани.",
                Names("map_family_parameters"), null, "changeSetId")
        };
    }

    private static string[] Names(params string[] names) => names;

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

    private static TaskVerdict Evaluate(FamilyTask task, IReadOnlyList<DogfoodCall> calls)
    {
        var call = Match(task, calls);
        if (call is null)
            return new TaskVerdict(false, "ожидаемый инструмент не вызван: " +
                                          string.Join(",", task.ExpectedTools));

        if (task.ExpectedCodes is { Length: > 0 })
        {
            if (call.Success)
            {
                return new TaskVerdict(false,
                    "ожидался отказ " + string.Join("/", task.ExpectedCodes) + ", получен успех");
            }

            if (!task.ExpectedCodes.Contains(call.ErrorCode ?? string.Empty, StringComparer.Ordinal))
            {
                return new TaskVerdict(false,
                    "ожидался " + string.Join("/", task.ExpectedCodes) + ", получен " +
                    (call.ErrorCode ?? "(нет кода)"));
            }
        }
        else if (!call.Success)
        {
            return new TaskVerdict(false, "инструмент вернул ошибку " +
                                          (call.ErrorCode ?? "(нет кода)"));
        }

        if (task.MustContain is { Length: > 0 } &&
            call.ResultJson.IndexOf(task.MustContain, StringComparison.Ordinal) < 0)
        {
            return new TaskVerdict(false, "в результате нет '" + task.MustContain + "'");
        }

        return new TaskVerdict(true, "ok (" + call.ToolName + ")");
    }

    private static DogfoodCall? Match(FamilyTask task, IReadOnlyList<DogfoodCall> calls)
    {
        var candidates = calls
            .Where(call =>
                task.ExpectedTools.Contains(call.ToolName, StringComparer.Ordinal) &&
                (task.ArgumentContains is not { Length: > 0 } needed ||
                 call.ArgumentsJson.IndexOf(needed, StringComparison.Ordinal) >= 0))
            .ToList();

        if (task.MustContain is { Length: > 0 } required)
        {
            var withContent = candidates
                .Where(call => call.ResultJson.IndexOf(required, StringComparison.Ordinal) >= 0)
                .ToList();
            if (withContent.Count > 0)
                candidates = withContent;
        }

        return candidates.LastOrDefault();
    }

    private static bool IsFirstTry(IReadOnlyList<DogfoodCall> calls, FamilyTask task)
    {
        if (calls.Count == 0)
            return false;

        if (Match(task, calls) is null)
            return false;

        foreach (var call in calls)
        {
            if (call.Success)
                continue;

            var allowed = task.ExpectedCodes is { Length: > 0 } &&
                          task.ExpectedCodes.Contains(
                              call.ErrorCode ?? string.Empty, StringComparer.Ordinal);
            if (!allowed)
                return false;
        }

        return true;
    }

    private static string SystemPrompt() =>
        "Ты SmartCon Agent внутри Autodesk Revit и работаешь с семействами через инструменты " +
        "пакета families. Отвечай по-русски. Помни: family ≠ типоразмер (FamilySymbol) ≠ " +
        "экземпляр (FamilyInstance). Сначала читай семейство (list_families, " +
        "list_family_types, get_family_parameters), не выдумывай имена и id. Числовые значения " +
        "всегда передавай с единицей измерения (unit: mm, m, deg, mm2, m3), иначе будет " +
        "unit_required. Запись по умолчанию идёт с dryRun=true; применяй (dryRun=false) только " +
        "если пользователь прямо просит сохранить/применить. Параметр, вычисляемый формулой, " +
        "нельзя переопределить значением или другой формулой (formula_not_allowed). Если " +
        "инструмент вернул ошибку, объясни её пользователю и не повторяй тот же вызов. " +
        "Отвечай кратко: что сделано и ключевые числа.";

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";

    private sealed record FamilyTask(
        string Prompt,
        string[] ExpectedTools,
        string[]? ExpectedCodes,
        string? MustContain,
        string? ArgumentContains = null);

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
}