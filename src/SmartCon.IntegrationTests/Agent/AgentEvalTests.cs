using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent.Eval;
using SmartCon.IntegrationTests.Agent.Eval;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Deterministic eval harness entry point (night run 2, phase A).
/// - SMARTCON_AGENT_EVAL_REPLAY=1: replay fixtures, no key/network needed.
/// - SMARTCON_AGENT_EVAL_RECORD=1: live recording into Agent/Eval/Fixtures.
/// - SMARTCON_AGENT_EVAL_ONLY=id1,id2: subset for fast iteration.
/// - SMARTCON_AGENT_EVAL_SOFT=1: record a baseline without failing the test on
///   threshold violations (hard invariants are still enforced).
/// </summary>
public sealed class AgentEvalTests : RevitApiTest
{
    [Test]
    public async Task Eval_Catalog_ReplayOrLive()
    {
        using var client = EvalLlmClient.TryCreate();
        if (client is null)
        {
            Skip.Test(
                "Нужен SMARTCON_AGENT_EVAL_REPLAY=1 (fixtures) или ключ в secrets.env " +
                "(SMARTCON_AGENT_EVAL_RECORD=1 / SMARTCON_AGENT_LIVE=1)");
            return;
        }

        var tasks = SelectTasks();
        if (tasks.Count == 0)
        {
            Skip.Test("SMARTCON_AGENT_EVAL_ONLY не совпал ни с одной задачей");
            return;
        }

        using var runner = new AgentEvalRunner(
            () => Application.NewProjectDocument(UnitSystem.Metric),
            client);

        var run = await runner.RunAsync(tasks, CancellationToken.None);

        Console.WriteLine(
            "Agent eval: " + run.Metrics.Passed + "/" + run.Metrics.Total +
            ", report: " + run.MarkdownPath);

        await Assert.That(run.Metrics.UnauthorizedWrites).IsEqualTo(0);
        await Assert.That(run.Metrics.SchemaValidCalls).IsEqualTo(run.Metrics.TotalCalls);

        if (!IsEnabled("SMARTCON_AGENT_EVAL_SOFT"))
        {
            await Assert.That(run.Metrics.ThresholdViolations).IsEmpty();
        }
    }

    private static IReadOnlyList<AgentEvalTask> SelectTasks()
    {
        var only = Environment.GetEnvironmentVariable("SMARTCON_AGENT_EVAL_ONLY");
        if (string.IsNullOrWhiteSpace(only))
            return AgentEvalTasks.All;

        var ids = new HashSet<string>(
            only!.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()),
            StringComparer.OrdinalIgnoreCase);

        return AgentEvalTasks.All
            .Where(task => ids.Contains(task.Id))
            .ToList();
    }

    private static bool IsEnabled(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
}
