using System.Text;
using System.Text.Json;
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

    // ── Сид на базе бенчмарка ───────────────────────────────────────────

    /// <summary>
    /// Seed gate for the non-live SeedVerifies runs: on the SMARTCON_BENCH_TEMPLATE
    /// base a seed failure is a FAIL with the collision named (readiness matrix);
    /// on the synthetic base the original skip semantics stay.
    /// </summary>
    private static void SeedOnBenchBase(string caseName, Action seed)
    {
        try
        {
            seed();
        }
        catch (Exception ex) when (BenchDocuments.TemplatePath() is not null)
        {
            throw new InvalidOperationException(
                caseName + ": сид не встал на базу SMARTCON_BENCH_TEMPLATE (" +
                BenchDocuments.TemplatePath() + "): " + ex.Message, ex);
        }
    }

    /// <summary>Seed gate for the live runs: a failed seed still writes a seed_failed artifact.</summary>
    private static void SeedOnBenchBaseLive(int caseNumber, string title, Action seed)
    {
        try
        {
            seed();
        }
        catch (Exception ex)
        {
            WriteSeedFailedArtifact(caseNumber, title, ex);
            throw;
        }
    }

    private static void WriteSeedFailedArtifact(int caseNumber, string title, Exception ex)
    {
        try
        {
            var settings = AgentLlmSettings.Load();
            var artifact = new BenchRunArtifact
            {
                CaseNumber = caseNumber,
                CaseKey = "case" + caseNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Title = title,
                Base = BenchRunArtifactWriter.BaseLabel(),
                Provider = settings.Provider,
                Model = settings.Model,
                Reasoning = settings.ReasoningEffort + "/" + settings.EffectiveReasoningFormat,
                MaxOutputTokens = settings.MaxOutputTokens,
                MaxRounds = BenchMaxRounds(),
                StartedUtc = DateTime.UtcNow,
                EndedUtc = DateTime.UtcNow,
                StopReason = "seed_failed",
                Error = ex.GetType().Name + ": " + ex.Message,
                Answer = null,
                Verdict = new BenchVerdictArtifact { Label = "FAIL", Score = 0, Detail = "seed failed" }
            };
            BenchRunArtifactWriter.Write(
                artifact,
                "# SmartCon production-loop benchmark, кейс " + caseNumber + " — SEED FAILED\n\n" +
                title + "\n\n" + artifact.Error + "\n");
        }
        catch (Exception artifactError)
        {
            Console.WriteLine("Seed-failed artifact write failed: " + artifactError.Message);
        }
    }

    // ── Продакшн-цикл одного кейса ──────────────────────────────────────

    private ProductionCaseRun RunProductionCase(
        int caseNumber,
        string title,
        string caseText,
        IReadOnlyList<string> seedSummary,
        Func<string?, List<BenchCall>, List<int>, CaseVerdict> evaluate,
        Func<string> dumpState,
        IReadOnlyList<string>? scriptedUserReplies = null)
    {
        var startedUtc = DateTime.UtcNow;
        var maxRounds = BenchMaxRounds();
        var maxSeconds = BenchMaxSeconds();
        using var host = ProductionAgentHost.Create(Doc);
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
        var progress = new InlineProgress(p =>
        {
            if (p.Kind != AgentProgressKind.ContextUsage)
                return;

            var cancel = false;
            lock (budgetGate)
            {
                llmRounds++;
                if (llmRounds > maxRounds && stopReason == "ok")
                {
                    stopReason = "round_budget";
                    cancel = true;
                    notes.Add("Round budget " + maxRounds + " превышен — цикл остановлен харнессом.");
                }
            }

            if (cancel)
                budgetCts.Cancel();
        });
        System.Threading.Timer? timeBudgetTimer = null;

        var intermediateAnswers = new List<string>();
        var turnResults = new List<AgentTurnResult>();
        var pendingReplies = new Queue<string>(scriptedUserReplies ?? Array.Empty<string>());
        var nextMessage = caseText;
        string? answer = null;

        while (true)
        {
            // Wall-clock budget: armed at the first RunTurnAsync; fires into the
            // same cancellation source as the round cap (stopReason = time_budget).
            if (timeBudgetTimer is null)
            {
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

            AgentTurnResult? turn;
            try
            {
                turn = host.Orchestrator
                    .RunTurnAsync(
                        conversationId,
                        nextMessage,
                        Array.Empty<AgentAttachment>(),
                        progress,
                        budgetCts.Token)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested)
            {
                break; // stopReason already "round_budget"
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
                nextMessage = pendingReplies.Dequeue();
                notes.Add("Scripted user reply: " + nextMessage);
                continue;
            }

            answer = turn.FinalText;
            break;
        }

        timeBudgetTimer?.Dispose();
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
            artifactCalls.Add(new BenchCallArtifact
            {
                Round = callRound,
                Seq = ++seq,
                Tool = record.ToolName,
                Args = Shorten(record.ArgumentsJson ?? string.Empty, 2000),
                Success = success,
                ErrorCode = record.Error,
                Message = ResultMessage(record.ResultJson),
                DurationMs = record.DurationMs
            });
        }

        var stateVerdict = EvaluateSafely(evaluate, answer, calls, callsPerRound);
        var overall = stateVerdict;
        if (stopReason != "ok")
            overall = new CaseVerdict(
                false,
                Math.Min(stateVerdict.Score, 0.5),
                stateVerdict.Detail + " [stop: " + stopReason + "]",
                stateVerdict.RedFlags);

        var promptTokens = turnResults.Sum(result => result.Usage.PromptTokens);
        var completionTokens = turnResults.Sum(result => result.Usage.CompletionTokens);
        var reasoningTokens = turnResults.Sum(result => result.Usage.ReasoningTokens);
        var cachedTokens = turnResults.Sum(result => result.Usage.CachedTokens);
        var endedUtc = DateTime.UtcNow;

        var transcript = BuildProductionTranscript(
            caseNumber, title, caseText, seedSummary, provider, model, reasoning,
            maxOutputTokens, maxRounds, maxSeconds, catalogSize, revitVersion, assistantTextByRound,
            artifactCalls, notes, answer, overall, stateVerdict, DumpStateSafely(dumpState),
            llmRounds, calls, promptTokens, completionTokens, startedUtc, endedUtc, stopReason, error);

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
            Rounds = llmRounds,
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

        return new ProductionCaseRun(
            overall, stateVerdict, answer, calls, callsPerRound, llmRounds, llmRounds,
            promptTokens, completionTokens, intermediateAnswers, stopReason, error,
            jsonPath, markdownPath);
    }

    private static CaseVerdict EvaluateSafely(
        Func<string?, List<BenchCall>, List<int>, CaseVerdict> evaluate,
        string? answer,
        List<BenchCall> calls,
        List<int> callsPerRound)
    {
        try
        {
            return evaluate(answer, calls, callsPerRound);
        }
        catch (Exception ex)
        {
            return new CaseVerdict(false, 0.0, "вердиктор упал: " + ex.GetType().Name + ": " + ex.Message,
                new[] { "verdict error" });
        }
    }

    private static string DumpStateSafely(Func<string> dumpState)
    {
        try
        {
            return dumpState();
        }
        catch (Exception ex)
        {
            return "state read-back упал: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static BenchVerdictArtifact ToArtifactVerdict(CaseVerdict verdict) =>
        new()
        {
            Label = verdict.Label,
            Score = verdict.Score,
            Detail = verdict.Detail,
            RedFlags = verdict.RedFlags
        };

    /// <summary>A non-dryRun call of a tool whose risk is not Read.</summary>
    private static bool IsRealWrite(BenchCall call, IReadOnlyDictionary<string, AgentToolRisk> toolRisk) =>
        IsNonReadTool(call, toolRisk) && !IsDryRunCall(call);

    private static bool IsNonReadTool(BenchCall call, IReadOnlyDictionary<string, AgentToolRisk> toolRisk) =>
        toolRisk.TryGetValue(call.ToolName, out var risk) && risk != AgentToolRisk.Read;

    private static bool VerifiedAfterLastWrite(List<BenchCall> calls, IReadOnlyDictionary<string, AgentToolRisk> toolRisk)
    {
        var lastWrite = -1;
        for (var index = 0; index < calls.Count; index++)
        {
            if (calls[index].Success && IsRealWrite(calls[index], toolRisk))
                lastWrite = index;
        }

        if (lastWrite < 0)
            return false;

        for (var index = lastWrite + 1; index < calls.Count; index++)
        {
            if (calls[index].Success &&
                toolRisk.TryGetValue(calls[index].ToolName, out var risk) &&
                risk == AgentToolRisk.Read)
                return true;
        }

        return false;
    }

    /// <summary>Progress, выполняющий обработчик синхронно в потоке оркестратора.</summary>
    private sealed class InlineProgress : IProgress<AgentProgress>
    {
        private readonly Action<AgentProgress> _handler;

        public InlineProgress(Action<AgentProgress> handler) => _handler = handler;

        public void Report(AgentProgress value) => _handler(value);
    }
}
