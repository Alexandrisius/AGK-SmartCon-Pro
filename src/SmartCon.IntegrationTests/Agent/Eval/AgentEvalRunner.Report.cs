using System.IO;
using System.Text;
using System.Text.Json;

namespace SmartCon.IntegrationTests.Agent.Eval;

internal sealed partial class AgentEvalRunner
{
    private string _revitVersion = string.Empty;

    private void WriteReport(AgentEvalRun run)
    {
        run = new AgentEvalRun
        {
            StartedAt = run.StartedAt,
            Model = run.Model,
            Provider = run.Provider,
            RevitVersion = _revitVersion,
            Replay = run.Replay,
            Results = run.Results,
            Metrics = run.Metrics,
            ReportDirectory = run.ReportDirectory
        };

        var markdown = run.Metrics.ToMarkdown(
            run.Model,
            run.Provider,
            string.IsNullOrEmpty(run.RevitVersion) ? "unknown" : run.RevitVersion,
            run.Results,
            run.StartedAt);

        var payload = new
        {
            startedAt = run.StartedAt.ToString("O"),
            model = run.Model,
            provider = run.Provider,
            revitVersion = run.RevitVersion,
            replay = run.Replay,
            metrics = new
            {
                total = run.Metrics.Total,
                passed = run.Metrics.Passed,
                successRate = run.Metrics.SuccessRate,
                readSuccessRate = run.Metrics.ReadSuccessRate,
                writeDryRunSuccessRate = run.Metrics.WriteDryRunSuccessRate,
                applyUndoSuccessRate = run.Metrics.ApplyUndoSuccessRate,
                toolSelectionAccuracy = run.Metrics.ToolSelectionAccuracy,
                routingAccuracy = run.Metrics.RoutingAccuracy,
                routingFallbackRate = run.Metrics.RoutingFallbackRate,
                totalCalls = run.Metrics.TotalCalls,
                schemaValidCalls = run.Metrics.SchemaValidCalls,
                schemaValidRate = run.Metrics.SchemaValidRate,
                dryRunFidelityRate = run.Metrics.DryRunFidelityRate,
                readBackMismatchRate = run.Metrics.ReadBackMismatchRate,
                unauthorizedWrites = run.Metrics.UnauthorizedWrites,
                averageLatencyMs = run.Metrics.AverageLatencyMs,
                p95LatencyMs = run.Metrics.P95LatencyMs,
                promptTokens = run.Metrics.PromptTokens,
                completionTokens = run.Metrics.CompletionTokens,
                thresholdsMet = run.Metrics.MeetsThresholds,
                thresholdViolations = run.Metrics.ThresholdViolations
            },
            tasks = run.Results.Select(result => new
            {
                id = result.Id,
                kind = result.Kind.ToString(),
                passed = result.Passed,
                failure = result.Failure,
                answer = result.Answer,
                llmRequests = result.LlmRequests,
                latencyMs = result.LatencyMs,
                promptTokens = result.PromptTokens,
                completionTokens = result.CompletionTokens,
                dryRunFidelity = result.DryRunFidelity,
                readBackMatched = result.ReadBackMatched,
                unauthorizedWrite = result.UnauthorizedWrite,
                routingHit = result.RoutingHit,
                routingFallback = result.RoutingFallback,
                calls = result.Calls.Select(call => new
                {
                    tool = call.ToolName,
                    dryRun = call.DryRun,
                    success = call.Success,
                    errorCode = call.ErrorCode,
                    affected = call.AffectedCount,
                    schemaValid = call.SchemaValid,
                    arguments = call.ArgumentsJson
                })
            })
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var markdownPath = Path.Combine(run.ReportDirectory, "report.md");
        var jsonPath = Path.Combine(run.ReportDirectory, "report.json");
        File.WriteAllText(markdownPath, markdown, new UTF8Encoding(false));
        File.WriteAllText(jsonPath, json, new UTF8Encoding(false));

        run.MarkdownPath = markdownPath;
        run.JsonPath = jsonPath;

        Console.WriteLine("Agent eval report: " + markdownPath);
        Console.WriteLine(
            "Agent eval: " + run.Metrics.Passed + "/" + run.Metrics.Total + " passed; read " +
            Percent(run.Metrics.ReadSuccessRate) + ", write " +
            Percent(run.Metrics.WriteDryRunSuccessRate) + ", apply+undo " +
            Percent(run.Metrics.ApplyUndoSuccessRate) + ", schema " +
            Percent(run.Metrics.SchemaValidRate) + ", unauthorized " +
            run.Metrics.UnauthorizedWrites + "; gate " +
            (run.Metrics.MeetsThresholds
                ? "PASS"
                : "FAIL (" + string.Join("; ", run.Metrics.ThresholdViolations) + ")"));
    }

    private static string Percent(double value) =>
        (value * 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
