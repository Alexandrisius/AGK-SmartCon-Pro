using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Production-loop runner of the real-cases benchmark: the verbatim case text
/// (NO starter block, NO harness system prompt) is the only user message of a
/// real conversation executed by <see cref="AgentOrchestrator"/> through
/// <see cref="ProductionAgentHost"/> — production system prompt, tool packs and
/// skills, LoopGuard, compaction and the production result budget. Every run
/// writes a JSON artifact and a transcript markdown under
/// <c>SMARTCON_BENCH_RESULTS_DIR</c> (or %LOCALAPPDATA%\Temp\smartcon-bench\results).
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private const int DefaultMaxRounds = 48;

    private sealed record ProductionCaseRun(
        CaseVerdict Verdict,
        CaseVerdict StateVerdict,
        string? Answer,
        List<BenchCall> Calls,
        List<int> CallsPerRound,
        int Rounds,
        int Requests,
        int PromptTokens,
        int CompletionTokens,
        IReadOnlyList<string> IntermediateAnswers,
        string StopReason,
        string? Error,
        string JsonPath,
        string MarkdownPath);

    /// <summary>
    /// Live guard of the production loop: SMARTCON_AGENT_LIVE=1 AND the
    /// production host can resolve a key (AgentLlmSettings + the same env
    /// overrides <see cref="ProductionAgentHost"/> applies).
    /// </summary>
    private static bool LiveConfigured()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SMARTCON_AGENT_LIVE"),
                "1",
                StringComparison.Ordinal))
            return false;

        var settings = AgentLlmSettings.Load();
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            return true;

        return new[]
            {
                "SMARTCON_AGENT_API_KEY",
                "OPENROUTER_API_KEY"
            }
            .Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));
    }

    /// <summary>Round budget of the harness (env SMARTCON_BENCH_MAX_ROUNDS, default 48).</summary>
    private static int BenchMaxRounds()
    {
        var raw = Environment.GetEnvironmentVariable("SMARTCON_BENCH_MAX_ROUNDS");
        return int.TryParse(raw, out var value) && value > 0 ? value : DefaultMaxRounds;
    }

    /// <summary>
    /// Wall-clock budget of one case in seconds (env
    /// SMARTCON_BENCH_MAX_SECONDS, default 900, clamped to 60..7200): fires from
    /// the first RunTurnAsync and cancels the same budget token as the round cap.
    /// </summary>
    private static int BenchMaxSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("SMARTCON_BENCH_MAX_SECONDS");
        return int.TryParse(raw, out var value) ? Math.Clamp(value, 60, 7200) : 900;
    }

    // ── Продакшн-цикл одного кейса ──────────────────────────────────────

    private ProductionCaseRun RunProductionCase(
        int caseNumber,
        string title,
        string caseText,
        IReadOnlyList<string> seedSummary,
        Func<string?, List<BenchCall>, List<int>, CaseVerdict> evaluate,
        Func<string> dumpState,
        IReadOnlyList<string>? scriptedUserReplies = null,
        IAgentLlmClient? llmClient = null)
    {
        var startedUtc = DateTime.UtcNow;
        var maxRounds = BenchMaxRounds();
        var maxSeconds = BenchMaxSeconds();
        var roundSteerAt = SteeringRoundThreshold(maxRounds);
        var timeSteerSeconds = SteeringTimeThresholdSeconds(maxSeconds);
        using var host = ProductionAgentHost.Create(Doc, llmClient);
        var conversationId = host.CreateConversation(title);

        // Inline progress: Report runs on the orchestrator thread, so the round
        // counter and the budget cancellation are observed mid-turn (the stock
        // Progress<T> posts asynchronously and would lag a round behind).
        var llmRounds = 0;
        string stopReason = "ok";
        string? error = null;
        var notes = new List<string>();
        var budgetGate = new object();
        using var budgetCts = new CancellationTokenSource();
        // Steering (C7-03/C2-02): при ~80% раундов / ~75% времени turn
        // останавливается мягко и модель получает предупреждение «заверши
        // работу» как сообщение пользователя ПОСЛЕ tool-ответов раунда
        // (между tool-вызовами сообщение вставить нельзя). Оба порога
        // срабатывают ТОЛЬКО на границе раунда (ContextUsage — момент прямо
        // перед LLM-запросом, где оркестратор первым делом проверяет токен):
        // ни один LLM-запрос не отправляется впустую, ни один tool не рвётся
        // посреди исполнения.
        var steeringSent = false;
        string? steeringReason = null;
        var finalCallUsed = false;

        // Таймер 75% времени только СТАВИТ флаг; отменяет граница раунда.
        var timeSteeringPending = false;

        // Отмена ТЕКУЩЕГО turn'а ради steering: источник живёт один turn.
        // Общий источник на все turn'ы оставался отменённым после первого
        // steering, и каждый следующий turn падал мгновенно (busy-loop до
        // жёсткого time budget — поймано оффлайн-тестом Budget_*).
        CancellationTokenSource? turnSteerSource = null;
        var turnSteered = false;
        var progress = new InlineProgress(p =>
        {
            if (p.Kind != AgentProgressKind.ContextUsage)
                return;

            var cancel = false;
            var steer = false;
            lock (budgetGate)
            {
                llmRounds++;
                if (llmRounds > maxRounds && stopReason == "ok")
                {
                    stopReason = "round_budget";
                    cancel = true;
                    notes.Add("Round budget " + maxRounds + " превышен — цикл остановлен харнессом.");
                }
                else if (stopReason == "ok" && !steeringSent &&
                         (llmRounds >= roundSteerAt || timeSteeringPending))
                {
                    steeringSent = true;
                    // При одновременном срабатывании честнее называть время:
                    // до жёсткого стопа по нему остаётся ~25% бюджета.
                    steeringReason = timeSteeringPending ? "time" : "rounds";
                    steer = true;
                    // Этот ContextUsage не привёл к LLM-запросу (оркестратор
                    // проверяет токен раньше): раунд не израсходован.
                    llmRounds--;
                    notes.Add(timeSteeringPending
                        ? "Steering по времени: " + timeSteerSeconds + "/" + maxSeconds +
                          " s (перед раундом " + (llmRounds + 1) + "/" + maxRounds + ")"
                        : "Steering по раундам: перед раундом " + (llmRounds + 1) + "/" +
                          maxRounds + " (порог " + roundSteerAt + ")");
                }
            }

            if (cancel)
                budgetCts.Cancel();
            if (steer)
            {
                turnSteered = true;
                try
                {
                    turnSteerSource?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Turn уже завершился и освободил источник.
                }
                catch (AggregateException)
                {
                    // Callback отмены бросил — сам turn всё равно отменён.
                }
            }
        });
        System.Threading.Timer? timeBudgetTimer = null;
        System.Threading.Timer? steeringTimer = null;

        var intermediateAnswers = new List<string>();
        var turnResults = new List<AgentTurnResult>();
        var pendingReplies = new Queue<string>(scriptedUserReplies ?? Array.Empty<string>());
        var nextMessage = caseText;
        string? answer = null;

        // Двухшаговый протокол (C4-05): число тул-вызовов, исполненных ДО
        // первого промежуточного ответа (граница «утверди у меня»), — вердиктор
        // кейса 9 штрафует реальные write-вызовы за этой чертой.
        var callsBeforeFirstIntermediate = -1;

        while (true)
        {
            // Wall-clock budget: armed at the first RunTurnAsync; fires into the
            // same cancellation source as the round cap (stopReason = time_budget).
            if (timeBudgetTimer is null)
            {
                // Steering-таймер: 75% времени — предупреждение модели (C7-03).
                steeringTimer = new System.Threading.Timer(_ =>
                {
                    lock (budgetGate)
                    {
                        if (stopReason == "ok" && !steeringSent && !timeSteeringPending)
                            timeSteeringPending = true;
                    }
                }, null, TimeSpan.FromSeconds(timeSteerSeconds), System.Threading.Timeout.InfiniteTimeSpan);

                timeBudgetTimer = new System.Threading.Timer(_ =>
                {
                    var cancel = false;
                    lock (budgetGate)
                    {
                        if (stopReason == "ok")
                        {
                            stopReason = "time_budget";
                            cancel = true;
                        }
                    }

                    if (cancel)
                    {
                        notes.Add("Time budget " + maxSeconds + " s exceeded — the harness stopped the cycle.");
                        try
                        {
                            budgetCts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // The turn finished and disposed the source.
                        }
                    }
                }, null, TimeSpan.FromSeconds(maxSeconds), System.Threading.Timeout.InfiniteTimeSpan);
            }

            // Своя отмена на каждый turn (освобождается в конце итерации).
            using var turnSteerCts = new CancellationTokenSource();
            turnSteerSource = turnSteerCts;
            turnSteered = false;
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(
                budgetCts.Token, turnSteerCts.Token);

            AgentTurnResult? turn;
            try
            {
                turn = PumpOnRevitThread(host.Orchestrator.RunTurnAsync(
                    conversationId,
                    nextMessage,
                    Array.Empty<AgentAttachment>(),
                    progress,
                    turnCts.Token));
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested)
            {
                // Бюджет исчерпан: turn отменён. Один финальный вызов БЕЗ
                // инструментов (C2-02): финальный ответ и Harness feedback не
                // теряются; stopReason остаётся round_budget|time_budget —
                // вердикт не выше PARTIAL, как и раньше.
                if (!finalCallUsed)
                {
                    finalCallUsed = true;
                    notes.Add("Бюджет исчерпан (" + stopReason +
                              ") — финальный вызов без инструментов за финальным ответом.");
                    answer = RunBudgetFinalAnswerTurn(host, conversationId, turnResults, notes);
                }

                break;
            }
            catch (OperationCanceledException) when (turnSteered)
            {
                // Steering: работа не отменяется — модель продолжает в том же
                // разговоре с предупреждением о закрывающемся бюджете; следующий
                // turn получает свежий (не отменённый) токен.
                nextMessage = steeringReason == "time" ? SteeringTimeText : SteeringRoundsText;
                continue;
            }
            catch (OperationCanceledException)
            {
                stopReason = "cancelled";
                break;
            }
            catch (Exception ex)
            {
                stopReason = "error";
                error = ex.GetType().Name + ": " + ex.Message;
                break;
            }

            turnResults.Add(turn);
            if (turn.FinalText.Contains("(loop_aborted)", StringComparison.Ordinal))
                stopReason = "loop_aborted";

            if (stopReason == "ok" && pendingReplies.Count > 0)
            {
                // Two-step case: the finished turn is the approval request — the
                // harness plays the user and the model continues in the same chat.
                intermediateAnswers.Add(turn.FinalText);
                if (callsBeforeFirstIntermediate < 0)
                {
                    // Из постоянного store (а не из вернувшихся turn'ов): turn,
                    // прерванный steering'ом, в turnResults не попадает.
                    callsBeforeFirstIntermediate = host.ReadToolCalls(conversationId).Count;
                }
                nextMessage = pendingReplies.Dequeue();
                notes.Add("Scripted user reply: " + nextMessage);
                continue;
            }

            answer = turn.FinalText;
            break;
        }

        timeBudgetTimer?.Dispose();
        steeringTimer?.Dispose();
        var messages = host.ReadConversation(conversationId);
        var toolRecords = host.ReadToolCalls(conversationId);
        var provider = host.Settings.Provider;
        var model = host.Settings.Model;
        var reasoning = host.Settings.ReasoningEffort + "/" + host.Settings.EffectiveReasoningFormat;
        var maxOutputTokens = host.Settings.MaxOutputTokens;
        var catalogSize = host.Registry.GetDefinitions().Count;
        var revitVersion = Doc.Application.VersionNumber;
        var toolRisk = host.Registry.GetDefinitions()
            .ToDictionary(definition => definition.Name, definition => definition.Risk, StringComparer.Ordinal);

        // Число LLM-запросов кейса — факт из истории (сохранённые assistant-
        // сообщения), без финального вызова: счётчик бюджета считает и запрос,
        // отменённый на границе (при round_budget он на 1 больше факта).
        var llmRequests = Math.Max(0,
            messages.Count(message => message.Role == AgentRole.Assistant) - (finalCallUsed ? 1 : 0));

        // Rounds from the persisted conversation: every assistant message with
        // tool calls is one LLM round; its ToolCalls ids map records to rounds.
        var roundByCallId = new Dictionary<string, int>(StringComparer.Ordinal);
        var assistantTextByRound = new Dictionary<int, string>();
        var callsPerRound = new List<int>();
        var round = 0;
        foreach (var message in messages)
        {
            if (message.Role != AgentRole.Assistant || message.ToolCalls.Count == 0)
                continue;

            round++;
            callsPerRound.Add(message.ToolCalls.Count);
            if (!string.IsNullOrWhiteSpace(message.Content))
                assistantTextByRound[round] = message.Content;
            foreach (var call in message.ToolCalls)
                roundByCallId[call.Id] = round;
        }

        var calls = new List<BenchCall>();
        var artifactCalls = new List<BenchCallArtifact>();
        var oversized = new List<(string RelativePath, string Text)>();
        var artifactStamp = "r" + startedUtc.ToString(
            "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var seq = 0;
        foreach (var record in toolRecords)
        {
            var callRound = roundByCallId.TryGetValue(record.Id, out var mapped) ? mapped : (round > 0 ? round : 1);
            var success = string.Equals(record.Status, "ok", StringComparison.Ordinal);
            calls.Add(new BenchCall(
                record.ToolName,
                record.ArgumentsJson ?? "{}",
                record.ResultJson ?? "{}",
                success,
                record.Error,
                callRound));

            // C2-13: значения до предела — целиком; длиннее — валидная
            // обёртка/маркер + ПОЛНЫЙ текст файлом рядом с артефактом.
            var callNumber = ++seq;
            var argsField = ArtifactJsonField(
                record.ArgumentsJson ?? string.Empty,
                OversizedFieldPath(artifactStamp, callNumber, "args.json"),
                oversized);
            var messageField = ArtifactTextField(
                ResultMessage(record.ResultJson),
                OversizedFieldPath(artifactStamp, callNumber, "message.txt"),
                oversized);
            artifactCalls.Add(new BenchCallArtifact
            {
                Round = callRound,
                Seq = callNumber,
                Tool = record.ToolName,
                Args = argsField,
                Success = success,
                ErrorCode = record.Error,
                Message = messageField,
                DurationMs = record.DurationMs
            });
        }

        // Контекст двухшагового протокола для вердикторов (C4-05): вердиктор
        // видит промежуточные ответы и черту «до одобрения».
        _lastRunIntermediateAnswers = intermediateAnswers;
        _lastRunCallsBeforeFirstIntermediate = callsBeforeFirstIntermediate;
        _lastRunToolRisk = toolRisk;

        var stateVerdict = EvaluateSafely(evaluate, EvaluationAnswer(answer), calls, callsPerRound);
        var overall = stateVerdict;
        if (stopReason != "ok")
            overall = new CaseVerdict(
                false,
                Math.Min(stateVerdict.Score, 0.5),
                stateVerdict.Detail + " [stop: " + stopReason + "]",
                stateVerdict.RedFlags);

        // Токены (C2-12): сумма по ВСЕМ завершённым LLM-вызовам из сессионной
        // базы (оркестратор пишет usage и отменённому по бюджету turn'у), не
        // только по вернувшимся turn'ам — иначе стоп по бюджету обнулял usage.
        var (usageFound, storePrompt, storeCompletion, storeReasoning, storeCached) =
            ReadTokenUsage(host.DatabasePath, conversationId.ToString());
        var promptTokens = usageFound
            ? storePrompt
            : turnResults.Sum(result => result.Usage.PromptTokens);
        var completionTokens = usageFound
            ? storeCompletion
            : turnResults.Sum(result => result.Usage.CompletionTokens);
        var reasoningTokens = usageFound
            ? storeReasoning
            : turnResults.Sum(result => result.Usage.ReasoningTokens);
        var cachedTokens = usageFound
            ? storeCached
            : turnResults.Sum(result => result.Usage.CachedTokens);
        var endedUtc = DateTime.UtcNow;

        var transcript = BuildProductionTranscript(
            caseNumber, title, caseText, seedSummary, provider, model, reasoning,
            maxOutputTokens, maxRounds, maxSeconds, catalogSize, revitVersion, assistantTextByRound,
            artifactCalls, notes, answer, overall, stateVerdict, DumpStateSafely(dumpState),
            llmRequests, calls, promptTokens, completionTokens, startedUtc, endedUtc, stopReason, error);

        var artifact = new BenchRunArtifact
        {
            CaseNumber = caseNumber,
            CaseKey = "case" + caseNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title = title,
            Base = BenchRunArtifactWriter.BaseLabel(),
            Provider = provider,
            Model = model,
            Reasoning = reasoning,
            MaxOutputTokens = maxOutputTokens,
            MaxRounds = maxRounds,
            MaxSeconds = maxSeconds,
            StartedUtc = startedUtc,
            EndedUtc = endedUtc,
            DurationSeconds = (endedUtc - startedUtc).TotalSeconds,
            Rounds = llmRequests,
            ToolCalls = calls.Count,
            ToolCallsByName = calls.GroupBy(call => call.ToolName)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            WriteCalls = calls.Count(call => IsRealWrite(call, toolRisk)),
            DryRunCalls = calls.Count(call => !IsRealWrite(call, toolRisk) && IsNonReadTool(call, toolRisk)),
            ErrorCallsByCode = calls.Where(call => call.ErrorCode is not null)
                .GroupBy(call => call.ErrorCode!)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ReasoningTokens = reasoningTokens,
            CachedTokens = cachedTokens,
            StopReason = stopReason,
            SteeringReason = steeringReason,
            BudgetFinalCall = finalCallUsed,
            UsageFromStore = usageFound,
            Error = error,
            Verdict = ToArtifactVerdict(overall),
            StateVerdict = ToArtifactVerdict(stateVerdict),
            Answer = answer,
            IntermediateAnswers = intermediateAnswers,
            Calls = artifactCalls,
            FirstWriteRound = calls.Where(call => IsRealWrite(call, toolRisk))
                .Select(call => (int?)call.Round).FirstOrDefault(),
            VerifiedAfterLastWrite = VerifiedAfterLastWrite(calls, toolRisk)
        };

        var (jsonPath, markdownPath) = BenchRunArtifactWriter.Write(artifact, transcript);
        WriteOversizedCallFields(jsonPath, oversized);
        // On dispose the session database lands next to the artifact (r<stamp>.db).
        host.ArtifactJsonPath = jsonPath;

        return new ProductionCaseRun(
            overall, stateVerdict, answer, calls, callsPerRound, llmRequests, llmRequests,
            promptTokens, completionTokens, intermediateAnswers, stopReason, error,
            jsonPath, markdownPath);
    }

}
