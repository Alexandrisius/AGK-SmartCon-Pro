using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Real-cases benchmark harness (#445): the case texts from the SSOT are sent
/// verbatim as the ONLY user message through the PRODUCTION agent loop
/// (<c>AgentOrchestrator</c> via <c>ProductionAgentHost</c> — production system
/// prompt, tool packs and skills, 40000 result budget, LoopGuard, compaction),
/// the model drives the real tools on a freshly seeded model, and the verdict
/// combines the model state with the call-log analysis. One case = one test =
/// one conversation on a fresh seed. Every run writes a JSON artifact and a
/// transcript markdown (SMARTCON_BENCH_RESULTS_DIR or
/// %LOCALAPPDATA%\Temp\smartcon-bench\results). Live tests are skipped unless
/// SMARTCON_AGENT_LIVE=1 and an API key are configured; the SeedVerifies tests
/// run offline. The seeded base can be the owner's real project via
/// SMARTCON_BENCH_TEMPLATE (see <see cref="BenchDocuments"/>).
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests : RevitApiTest
{
    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;

    private Document Doc => _document!;

    [After(Test)]
    public void Close()
    {
        _document?.Close(false);
        BenchDocuments.DeleteTrackedCopies();
    }

    /// <summary>Вердикт одного кейса: состояние модели + анализ лога вызовов.</summary>
    private sealed record CaseVerdict(
        bool Passed,
        double Score,
        string Detail,
        IReadOnlyList<string> RedFlags)
    {
        public string Label =>
            Score >= 1.0 ? "PASS" : Score > 0.0 ? "PARTIAL" : "FAIL";
    }

    /// <summary>Один вызов тула в порядке исполнения (из tool_calls продакшн-стора).</summary>
    internal sealed record BenchCall(
        string ToolName,
        string ArgumentsJson,
        string ResultJson,
        bool Success,
        string? ErrorCode,
        int Round);

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";
}
