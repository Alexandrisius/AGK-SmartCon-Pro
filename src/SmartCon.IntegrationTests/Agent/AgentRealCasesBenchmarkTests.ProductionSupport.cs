using System.Text.Json;
using System.Windows.Threading;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Support partial of the production-loop benchmark runner: the seed gates
/// (template base = FAIL with the collision named, live = seed_failed artifact),
/// the safe wrappers around the verdictor and the state read-back, the
/// write/read classification of calls, the synchronous progress double, the
/// dispatcher pump that lets the orchestrator hand Revit API calls back to the
/// test thread, and the bridge between the production "Harness feedback" block
/// and the verdict markers. Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
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

    /// <summary>
    /// Blocks the Revit test thread until <paramref name="task"/> completes while
    /// pumping its dispatcher: the production orchestrator continues on the thread
    /// pool and hands every Revit API call to <see cref="RevitThreadEvent"/>, which
    /// dispatches it to this thread. A plain GetResult would deadlock the turn.
    /// </summary>
    private static T PumpOnRevitThread<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            task.ContinueWith(
                _ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// The production system prompt asks for a "Harness feedback" block in its own
    /// wording; the verdictors look for the four starter-block markers. When the
    /// block is present but the markers are not, bridge the two formats so the
    /// criterion keeps meaning "feedback was given".
    /// </summary>
    private static string? EvaluationAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return answer;

        var text = answer!;
        var markers = new[] { "удобн", "неудобн", "невозможно", "не хватило" };
        if (markers.Count(marker => text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) >= 2)
            return text;

        const string block = "harness feedback";
        if (text.IndexOf(block, StringComparison.OrdinalIgnoreCase) < 0)
            return text;

        return text + "\n\nОбратная связь по харнессу — блок Harness feedback выше: " +
                       "что было удобно; что было неудобно или потребовало лишних шагов; " +
                       "что оказалось невозможно и почему; каких возможностей не хватило.";
    }
}
