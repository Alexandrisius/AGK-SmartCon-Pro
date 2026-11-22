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
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Real-cases benchmark harness (#445, task 3 of the owner plan): the case texts
/// from docs/agent/archive/00-strategy/real-cases-benchmark.md are sent verbatim
/// to a live LLM (starter block + case text as ONE user message), the model
/// drives the real tools on a freshly seeded model, and the verdict combines the
/// model state with the call-log analysis. One case = one test = one dialog on a
/// fresh seed. Asserts are soft: the test only fails when the run did not happen
/// at all (LLM unreachable, seed broken); verdicts and red flags land in the
/// transcript (%TEMP%\smartcon-bench-dogfood\caseN-transcript.md plus a stable
/// copy in %LOCALAPPDATA%\Temp\smartcon-bench-dogfood-stable, because Nice3point
/// substitutes TEMP with a GUID directory). Skipped unless SMARTCON_AGENT_LIVE=1
/// and an API key are configured.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests : RevitApiTest
{
    // No product turn limits (owner policy): ни раундов, ни времени. This harness
    // cap only guards test runtime: case 3 with a diligent dry-run → apply →
    // read-back → fix cycle needed 8+ rounds (first live run #445 ran out at 7
    // mid-fix, which measured the cap, not the model). Case 5 re-measured it
    // (2026-11-21): a 12-family migration spends 3–4 rounds on reads plus 5+ on
    // the job lifecycle, and both live runs hit 10 before the final report —
    // the model was mid-work, so the cap graded itself again.
    private const int MaxRoundsPerTask = 32;

    /// <summary>Стартовый блок теста из SSOT — дословно, добавляется к каждому кейсу.</summary>
    private const string StarterBlock =
        "Это тестовый прогон ИИ-агента, а не рабочая смена. Выполни задачу так, как сделал бы " +
        "её для реального проекта, и в конце отдельным блоком дай обратную связь:\n" +
        "— что было удобно;\n" +
        "— что было неудобно или потребовало лишних шагов;\n" +
        "— что оказалось невозможно и почему;\n" +
        "— каких возможностей не хватило (опиши задачу словами, без имён инструментов).";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;

    private Document Doc => _document!;

    [After(Test)]
    public void Close()
    {
        _document?.Close(false);
    }

    // ── Общий каркас одного кейса ───────────────────────────────────────

    private sealed record CaseRun(
        CaseVerdict Verdict,
        string? Answer,
        List<BenchCall> Calls,
        List<int> CallsPerRound,
        int Requests,
        int PromptTokens,
        int CompletionTokens,
        IReadOnlyList<string> IntermediateAnswers);

    private sealed record CaseVerdict(
        bool Passed,
        double Score,
        string Detail,
        IReadOnlyList<string> RedFlags)
    {
        public string Label =>
            Score >= 1.0 ? "PASS" : Score > 0.0 ? "PARTIAL" : "FAIL";
    }

    private CaseRun RunCase(
        int caseNumber,
        string title,
        string caseText,
        DogfoodConfig config,
        IReadOnlyList<IAgentTool> tools,
        IReadOnlyList<string> seedSummary,
        Func<string?, List<BenchCall>, List<int>, CaseVerdict> evaluate,
        Func<string> dumpState,
        IReadOnlyList<string>? scriptedUserReplies = null)
    {
        var definitions = tools.Select(tool => tool.Definition).ToList();
        var byName = tools.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);

        var transcript = new StringBuilder();
        transcript.AppendLine("# SmartCon Agent — real-cases benchmark (#445), кейс " + caseNumber);
        transcript.AppendLine();
        transcript.AppendLine("- Title: " + title);
        transcript.AppendLine("- Provider: " + config.Provider);
        transcript.AppendLine("- Model: " + config.Model);
        transcript.AppendLine("- Revit: " + Doc.Application.VersionNumber);
        transcript.AppendLine("- Started: " + DateTime.Now.ToString("O"));
        transcript.AppendLine("- Tools: " + definitions.Count + " (" +
                              string.Join(", ", definitions.Select(d => d.Name)) + ")");
        transcript.AppendLine("- Seeded model:");
        foreach (var line in seedSummary)
            transcript.AppendLine("  - " + line);
        transcript.AppendLine();
        transcript.AppendLine("## User prompt (дословно из SSOT)");
        transcript.AppendLine();
        transcript.AppendLine("> " + StarterBlock.Replace("\n", "\n> "));
        transcript.AppendLine("> " + caseText);
        transcript.AppendLine();

        // 240s: the zai endpoint stalled twice for >120s on the round-3 request
        // (live runs 2026-11-21 19:07/19:13); the product has no time limits
        // (owner policy), and the harness timeout only guards against hangs.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(240) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);

        var messages = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "system", ["content"] = SystemPrompt() },
            new() { ["role"] = "user", ["content"] = StarterBlock + "\n\n" + caseText }
        };

        var calls = new List<BenchCall>();
        var callsPerRound = new List<int>();
        var failureStreaks = new Dictionary<string, int>(StringComparer.Ordinal);
        var steeredStreaks = new HashSet<string>(StringComparer.Ordinal);
        var toolCallCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var steeredStorms = new HashSet<string>(StringComparer.Ordinal);
        var dryStreaks = new Dictionary<string, int>(StringComparer.Ordinal);
        var steeredDryRuns = new HashSet<string>(StringComparer.Ordinal);
        var abortedStreaks = new Dictionary<string, int>(StringComparer.Ordinal);
        var steeredAborts = new HashSet<string>(StringComparer.Ordinal);
        var intermediateAnswers = new List<string>();
        string? answer = null;
        var requests = 0;
        var promptTokens = 0;
        var completionTokens = 0;
        var scriptedReplyIndex = 0;
        var emptyAnswers = 0;

        transcript.AppendLine("## Dialog");
        transcript.AppendLine();

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
                // Two-step cases (v2 case 4): the model first shows a list for
                // approval (a reply with no tool calls). When a scripted user
                // reply is still unused, the harness plays the user, keeps the
                // intermediate answer and lets the model continue the work.
                if (scriptedUserReplies is { Count: > 0 } && scriptedReplyIndex < scriptedUserReplies.Count)
                {
                    var reply = scriptedUserReplies[scriptedReplyIndex];
                    scriptedReplyIndex++;

                    intermediateAnswers.Add(response.Content ?? string.Empty);
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = response.Content
                    });
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = reply
                    });

                    transcript.AppendLine("**Intermediate answer " + scriptedReplyIndex + ":** " +
                                          Shorten(response.Content ?? string.Empty, 600));
                    transcript.AppendLine();
                    transcript.AppendLine("**Scripted user reply " + scriptedReplyIndex + ":** " + reply);
                    transcript.AppendLine();
                    continue;
                }

                // An EMPTY no-tool response is an LLM glitch (observed after a
                // heavy parallel fan-out, live case 5 rerun), not a final
                // answer: keep the (valid, possibly empty) assistant message
                // and let the model continue. Give up after 3 in a row.
                if (string.IsNullOrWhiteSpace(response.Content) && emptyAnswers < 3)
                {
                    emptyAnswers++;
                    transcript.AppendLine("**Empty response " + emptyAnswers +
                                          "/3 — treating as transient and continuing.**");
                    transcript.AppendLine();
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = response.Content
                    });
                    continue;
                }

                answer = response.Content;
                break;
            }

            emptyAnswers = 0;
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

            // Steering messages must never interleave with tool responses (a user
            // message between tool answers is protocol-invalid): they are queued
            // here and appended AFTER every tool response of the round.
            var pendingSteering = new List<(string Text, string Label)>();

            foreach (var call in response.ToolCalls)
            {
                var resultJson = ExecuteTool(call, byName);
                var record = BenchCall.From(call, resultJson, round);
                calls.Add(record);

                // Soft loop guard (mirrors the production LoopGuard): a tool
                // failing four times in a row with the same error gets one
                // steering user message instead of an endless retry storm.
                if (!record.Success && record.ErrorCode is not null)
                {
                    var streakKey = call.Name + ":" + record.ErrorCode;
                    failureStreaks[streakKey] = failureStreaks.TryGetValue(streakKey, out var s) ? s + 1 : 1;
                    if (failureStreaks[streakKey] >= 4 && steeredStreaks.Add(streakKey))
                    {
                        pendingSteering.Add((
                            "Инструмент " + call.Name + " падает с ошибкой '" +
                            record.ErrorCode + "' повторно. Не повторяй тот же вызов: " +
                            "смени подход (меньше аргументов, другой инструмент) или честно " +
                            "сообщи, что не получилось.",
                            "Loop-guard steering injected for " + streakKey + "."));
                    }
                }
                else if (record.Success)
                {
                    failureStreaks.Clear();
                }

                toolCallCounts[call.Name] = toolCallCounts.TryGetValue(call.Name, out var c) ? c + 1 : 1;
                // Catalog search is cheap but its storms (deepseek: 13 find_tools)
                // burn rounds without changing the toolset — steer earlier.
                var stormThreshold = call.Name == "find_tools" ? 8 : 18;
                if (toolCallCounts[call.Name] > stormThreshold && steeredStorms.Add(call.Name))
                {
                    pendingSteering.Add((
                        "Ты вызвал " + call.Name + " уже " +
                        toolCallCounts[call.Name] + " раз. Данных достаточно: " +
                        "переходи к применению результата и итоговому ответу, " +
                        "не продолжай перезапрашивать то же самое.",
                        "Storm steering injected for " + call.Name + "."));
                }

                // Dry-run ping-pong steering (live case 5, glm-5.3): the model
                // re-runs a successful dry run of the same write tool without
                // ever applying. After the second consecutive success nudge it
                // to repeat the call with dryRun:false.
                if (record.Success && (ContainsText(call.ArgumentsJson, "\"dryRun\":true") ||
                                       ContainsText(call.ArgumentsJson, "\"dryRun\": true")))
                {
                    dryStreaks[call.Name] = dryStreaks.TryGetValue(call.Name, out var d) ? d + 1 : 1;
                    if (dryStreaks[call.Name] >= 2 && steeredDryRuns.Add(call.Name))
                    {
                        pendingSteering.Add((
                            "Прогон " + call.Name + " с dryRun:true уже успешен " +
                            dryStreaks[call.Name] + " раза подряд — план подтверждён. " +
                            "Повтори ТОТ ЖЕ вызов с dryRun:false, чтобы применить изменения.",
                            "Dry-run steering injected for " + call.Name + "."));
                    }
                }
                else if (record.Success && (ContainsText(call.ArgumentsJson, "\"dryRun\":false") ||
                                            ContainsText(call.ArgumentsJson, "\"dryRun\": false")))
                {
                    dryStreaks.Remove(call.Name);
                }

                // Atomic-abort steering: the model re-sends the same batch after
                // two whole-batch rollbacks instead of dropping the failed items.
                if (!record.Success && record.ErrorCode == "atomic_aborted")
                {
                    abortedStreaks[call.Name] = abortedStreaks.TryGetValue(call.Name, out var a) ? a + 1 : 1;
                    if (abortedStreaks[call.Name] >= 2 && steeredAborts.Add(call.Name))
                    {
                        pendingSteering.Add((
                            "Инструмент " + call.Name + " дважды откатил батч целиком (atomic). " +
                            "Исключи из items элементы из failed[] последнего ответа или убери " +
                            "atomic:true и повтори с dryRun:false — не отправляй тот же батч третий раз.",
                            "Atomic-abort steering injected for " + call.Name + "."));
                    }
                }

                transcript.AppendLine(
                    "- r" + round + " `" + call.Name + "` " + Shorten(call.ArgumentsJson, 240));
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

            foreach (var steer in pendingSteering)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = steer.Text
                });
                transcript.AppendLine("**" + steer.Label + "**");
                transcript.AppendLine();
            }
        }

        if (answer is null && callsPerRound.Count >= MaxRoundsPerTask)
            transcript.AppendLine("(достигнут лимит раундов " + MaxRoundsPerTask +
                                  " — финального ответа не было)");

        transcript.AppendLine();
        transcript.AppendLine("## Answer");
        transcript.AppendLine();
        transcript.AppendLine(answer ?? "(no final answer)");
        transcript.AppendLine();

        var verdict = evaluate(answer, calls, callsPerRound);
        transcript.AppendLine("## Verdict");
        transcript.AppendLine();
        transcript.AppendLine("**" + verdict.Label + "** (score " +
                              verdict.Score.ToString("0.0",
                                  System.Globalization.CultureInfo.InvariantCulture) + ") — " +
                              verdict.Detail);
        if (verdict.RedFlags.Count > 0)
        {
            transcript.AppendLine();
            transcript.AppendLine("Red flags:");
            foreach (var flag in verdict.RedFlags)
                transcript.AppendLine("- " + flag);
        }

        transcript.AppendLine();
        transcript.AppendLine("## State read-back");
        transcript.AppendLine();
        transcript.AppendLine(dumpState());
        transcript.AppendLine();

        transcript.AppendLine("## Summary");
        transcript.AppendLine();
        transcript.AppendLine("- Rounds used: " + callsPerRound.Count + " (cap " + MaxRoundsPerTask + ")");
        transcript.AppendLine("- Tool calls: " + calls.Count + " (" +
                              string.Join(" → ", calls.Select(c =>
                                  c.ToolName + (c.Success ? string.Empty : "(!" + c.ErrorCode + ")"))) + ")");
        transcript.AppendLine("- Final answer: " + (string.IsNullOrWhiteSpace(answer) ? "no" : "yes"));
        transcript.AppendLine("- Intermediate answers: " + intermediateAnswers.Count +
                              " (scripted user replies: " + scriptedReplyIndex + ")");
        transcript.AppendLine("- LLM requests: " + requests);
        transcript.AppendLine("- Tokens: prompt " + promptTokens + ", completion " + completionTokens);
        transcript.AppendLine("- Finished: " + DateTime.Now.ToString("O"));

        WriteTranscript(caseNumber, transcript.ToString());

        return new CaseRun(
            verdict, answer, calls, callsPerRound, requests, promptTokens, completionTokens,
            intermediateAnswers);
    }

    private static void WriteTranscript(int caseNumber, string content)
    {
        var fileName = "case" + caseNumber + "-transcript.md";

        var directory = Path.Combine(Path.GetTempPath(), "smartcon-bench-dogfood");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);
        Console.WriteLine("Real-cases benchmark transcript (case " + caseNumber + "): " + path);

        try
        {
            // Nice3point substitutes TEMP with a GUID directory (#445): keep a
            // stable copy the operator can open.
            var stableDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", "smartcon-bench-dogfood-stable");
            Directory.CreateDirectory(stableDirectory);
            var destination = Path.Combine(stableDirectory, fileName);
            File.Copy(path, destination, overwrite: true);
            Console.WriteLine("Real-cases benchmark stable transcript: " + destination);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Stable transcript copy failed: " + ex.GetType().Name + ": " +
                              ex.Message);
        }
    }

    /// <summary>
    /// Neutral system prompt: general tool discipline only, no hints for the
    /// specific case (the benchmark measures how the model approaches the task,
    /// SSOT: «как агент к ней придёт — его решение»).
    /// </summary>
    private static string SystemPrompt() =>
        "Ты SmartCon Agent внутри Autodesk Revit и помогаешь проектировщику работать с моделью: " +
        "виды, шаблоны, спецификации, фильтры, параметры, семейства. Отвечай по-русски. " +
        "Сначала изучай проект инструментами чтения и опирайся только на их результаты — " +
        "не выдумывай имена, id и параметры, бери их из ответов инструментов. Операции записи " +
        "по умолчанию идут с dryRun=true (проверка без изменений); когда пользователь прямо " +
        "просит выполнить — примени (dryRun=false) и подтверждай результат чтением. Откат " +
        "последней применённой операции — undo_last. Несколько независимых вызовов можно " +
        "объединять в один ответ. Если инструмент вернул ошибку — объясни её пользователю и не " +
        "повторяй тот же вызов без изменений. Если задачу нельзя решить имеющимися " +
        "инструментами — честно скажи об этом и опиши словами, чего не хватило. " +
        "Отвечай кратко: что сделано и ключевые числа.";

    // ── Исполнение тулов и записи вызовов ───────────────────────────────

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
            var truncated = ex.Message.Contains("end of string") ||
                            ex.Message.Contains("end of data");
            var hint = truncated
                ? " Arguments JSON looks TRUNCATED (parallel tool calls hit the output limit): reduce the argument size, split the work, or use a compact mode (e.g. classifierPath instead of inline items)."
                : string.Empty;
            return "{\"success\":false,\"errorCode\":\"invalid_arguments\",\"message\":\"" +
                   ex.Message.Replace("\"", "'") + hint + "\"}";
        }

        var result = tool switch
        {
            AgentWriteToolBase write => write.ExecuteForTests(args, approved: true, CancellationToken.None),
            AgentReadToolBase read => read.ExecuteForTests(Doc, args, CancellationToken.None),
            IAgentTool plain => ExecutePlainTool(plain, args),
            _ => null
        };

        return result is null
            ? "{\"success\":false,\"errorCode\":\"unknown_tool\",\"message\":\"Tool cannot be executed\"}"
            : ToolResultBudget.Serialize(result, 8000);
    }

    private ToolResult ExecutePlainTool(IAgentTool tool, JsonElement args) =>
        tool.ExecuteAsync(
                // Real document identity: the job spec validator rejects an empty
                // documentId ("documentId is required"), and the first live case 5
                // run burned 6 rounds on that harness artifact (#445) — job tools
                // need the same context the production executor provides.
                new AgentToolContext(
                    new InlineAwaitableEvent(), AgentDocumentIdentity.GetDocumentId(Doc), Guid.Empty)
                {
                    WriteApproved = true
                },
                args,
                CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    private sealed record BenchCall(
        string ToolName,
        string ArgumentsJson,
        string ResultJson,
        bool Success,
        string? ErrorCode,
        int Round)
    {
        public static BenchCall From(DogfoodToolCall call, string resultJson, int round)
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

            return new BenchCall(
                call.Name, call.ArgumentsJson, resultJson, success, errorCode, round);
        }
    }

    private sealed class InlineAwaitableEvent : IAgentAwaitableEvent
    {
        private static readonly object App = new();

        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default) =>
            Task.FromResult(funcWithApp(App));

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            actionWithApp(App);
            return Task.CompletedTask;
        }

        public void ProcessQueue(object revitApp)
        {
        }

        public void Initialize(Action onRaise)
        {
        }
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";
}
