using System.Globalization;
using System.Text;

namespace SmartCon.Core.Models.Agent.Eval;

/// <summary>One executed tool call recorded by the eval runner.</summary>
public sealed class AgentEvalCall
{
    public required string ToolName { get; init; }

    public required string ArgumentsJson { get; init; }

    public bool Success { get; init; }

    public string? ErrorCode { get; init; }

    public int AffectedCount { get; init; }

    public bool DryRun { get; init; }

    public bool SchemaValid { get; init; } = true;

    public string? ResultJson { get; init; }
}

/// <summary>Outcome of one eval task.</summary>
public sealed class AgentEvalTaskResult
{
    public required string Id { get; init; }

    public required AgentEvalTaskKind Kind { get; init; }

    public bool Passed { get; set; }

    public string? Failure { get; set; }

    public List<AgentEvalCall> Calls { get; } = new();

    public string? Answer { get; set; }

    public int LlmRequests { get; set; }

    public long LatencyMs { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    /// <summary>Dry-run changed count matched the expectation.</summary>
    public bool DryRunFidelity { get; set; } = true;

    /// <summary>Null for tasks without a read-back phase.</summary>
    public bool? ReadBackMatched { get; set; }

    /// <summary>A write with dryRun=false happened in a dry-run-only task.</summary>
    public bool UnauthorizedWrite { get; set; }

    /// <summary>All expected tools were present in the router shortlist.</summary>
    public bool RoutingHit { get; set; } = true;

    /// <summary>The router had no signal and fell back to the full catalog.</summary>
    public bool RoutingFallback { get; set; }
}

/// <summary>
/// Deterministic aggregation of eval results plus the night-run threshold gate
/// (read ≥ 85 %, write dry-run ≥ 80 %, schema-valid = 100 %, unauthorized
/// writes = 0, apply+undo ≥ 80 %).
/// </summary>
public sealed record AgentEvalMetrics
{
    public int Total { get; init; }

    public int ReadTasks { get; init; }

    public int WriteDryRunTasks { get; init; }

    public int ApplyUndoTasks { get; init; }

    public int Passed { get; init; }

    public double SuccessRate { get; init; }

    public double ReadSuccessRate { get; init; }

    public double WriteDryRunSuccessRate { get; init; }

    public double ApplyUndoSuccessRate { get; init; }

    public double ToolSelectionAccuracy { get; init; }

    public double RoutingAccuracy { get; init; }

    public double RoutingFallbackRate { get; init; }

    public int TotalCalls { get; init; }

    public int SchemaValidCalls { get; init; }

    public double SchemaValidRate { get; init; }

    public double DryRunFidelityRate { get; init; }

    public double ReadBackMismatchRate { get; init; }

    public int UnauthorizedWrites { get; init; }

    public double AverageLatencyMs { get; init; }

    public double P95LatencyMs { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public IReadOnlyList<string> ThresholdViolations { get; init; } = Array.Empty<string>();

    public bool MeetsThresholds => ThresholdViolations.Count == 0;

    public static AgentEvalMetrics Compute(
        IReadOnlyList<AgentEvalTask> tasks,
        IReadOnlyList<AgentEvalTaskResult> results)
    {
        var byId = results.ToDictionary(result => result.Id, StringComparer.Ordinal);
        var read = tasks.Where(task => task.Kind == AgentEvalTaskKind.Read).ToList();
        var dry = tasks.Where(task => task.Kind == AgentEvalTaskKind.WriteDryRun).ToList();
        var apply = tasks.Where(task => task.Kind == AgentEvalTaskKind.WriteApplyUndo).ToList();

        int passedIn(IReadOnlyList<AgentEvalTask> group) =>
            group.Count(task => byId.TryGetValue(task.Id, out var result) && result.Passed);

        double rate(int passed, int total) => total == 0 ? 0 : (double)passed / total;

        var totalPassed = results.Count(result => result.Passed);
        var toolSelection = tasks.Count(task =>
            byId.TryGetValue(task.Id, out var result)
            && task.ExpectedTools.All(expected =>
                result.Calls.Any(call => call.Success
                    && string.Equals(call.ToolName, expected, StringComparison.Ordinal))));

        var calls = results.SelectMany(result => result.Calls).ToList();
        var schemaValid = calls.Count(call => call.SchemaValid);

        var writeTasks = dry.Concat(apply).ToList();
        var fidelity = writeTasks.Count(task =>
            byId.TryGetValue(task.Id, out var result) && result.DryRunFidelity);

        var readBackChecked = apply
            .Select(task => byId.TryGetValue(task.Id, out var result) ? result : null)
            .Where(result => result?.ReadBackMatched is not null)
            .ToList();
        var readBackMismatches = readBackChecked.Count(result => result!.ReadBackMatched == false);

        var unauthorized = results.Count(result => result.UnauthorizedWrite);
        var routingHits = results.Count(result => result.RoutingHit);
        var routingFallbacks = results.Count(result => result.RoutingFallback);
        var latencies = results.Select(result => (double)result.LatencyMs).OrderBy(value => value).ToList();

        var metrics = new AgentEvalMetrics
        {
            Total = tasks.Count,
            ReadTasks = read.Count,
            WriteDryRunTasks = dry.Count,
            ApplyUndoTasks = apply.Count,
            Passed = totalPassed,
            SuccessRate = rate(totalPassed, tasks.Count),
            ReadSuccessRate = rate(passedIn(read), read.Count),
            WriteDryRunSuccessRate = rate(passedIn(dry), dry.Count),
            ApplyUndoSuccessRate = rate(passedIn(apply), apply.Count),
            ToolSelectionAccuracy = rate(toolSelection, tasks.Count),
            RoutingAccuracy = rate(routingHits, results.Count),
            RoutingFallbackRate = rate(routingFallbacks, results.Count),
            TotalCalls = calls.Count,
            SchemaValidCalls = schemaValid,
            SchemaValidRate = calls.Count == 0 ? 1 : rate(schemaValid, calls.Count),
            DryRunFidelityRate = writeTasks.Count == 0 ? 1 : rate(fidelity, writeTasks.Count),
            ReadBackMismatchRate = rate(readBackMismatches, readBackChecked.Count),
            UnauthorizedWrites = unauthorized,
            AverageLatencyMs = latencies.Count == 0 ? 0 : latencies.Average(),
            P95LatencyMs = Percentile(latencies, 0.95),
            PromptTokens = results.Sum(result => (long)result.PromptTokens),
            CompletionTokens = results.Sum(result => (long)result.CompletionTokens)
        };

        return metrics with { ThresholdViolations = EvaluateThresholds(metrics) };
    }

    private static IReadOnlyList<string> EvaluateThresholds(AgentEvalMetrics metrics)
    {
        var violations = new List<string>();
        if (metrics.ReadTasks > 0 && metrics.ReadSuccessRate < 0.85)
            violations.Add(
                "read success " + Percent(metrics.ReadSuccessRate) + " < 85 %");
        if (metrics.WriteDryRunTasks > 0 && metrics.WriteDryRunSuccessRate < 0.80)
            violations.Add(
                "write dry-run success " + Percent(metrics.WriteDryRunSuccessRate) + " < 80 %");
        if (metrics.ApplyUndoTasks > 0 && metrics.ApplyUndoSuccessRate < 0.80)
            violations.Add(
                "apply+undo success " + Percent(metrics.ApplyUndoSuccessRate) + " < 80 %");
        if (metrics.SchemaValidRate < 1.0)
            violations.Add(
                "schema-valid calls " + Percent(metrics.SchemaValidRate) + " < 100 %");
        if (metrics.UnauthorizedWrites > 0)
            violations.Add(metrics.UnauthorizedWrites + " unauthorized write(s)");
        return violations;
    }

    public string ToMarkdown(
        string model,
        string provider,
        string revitVersion,
        IReadOnlyList<AgentEvalTaskResult> results,
        DateTime startedAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SmartCon Agent eval — " +
                           startedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine("| Parameter | Value |");
        builder.AppendLine("|---|---|");
        builder.AppendLine("| Provider / model | " + provider + " / " + model + " |");
        builder.AppendLine("| Revit | " + revitVersion + " |");
        builder.AppendLine("| Tasks | " + Total + " |");
        builder.AppendLine("| Passed | " + Passed + " (" + Percent(SuccessRate) + ") |");
        builder.AppendLine("| Read success | " + Percent(ReadSuccessRate) + " (gate ≥ 85 %) |");
        builder.AppendLine("| Write dry-run success | " + Percent(WriteDryRunSuccessRate) +
                           " (gate ≥ 80 %) |");
        builder.AppendLine("| Apply+undo success | " + Percent(ApplyUndoSuccessRate) +
                           " (gate ≥ 80 %) |");
        builder.AppendLine("| Tool selection accuracy | " + Percent(ToolSelectionAccuracy) + " |");
        builder.AppendLine("| Routing accuracy (shortlist) | " + Percent(RoutingAccuracy) +
                           "; fallback " + Percent(RoutingFallbackRate) + " |");
        builder.AppendLine("| Schema-valid calls | " + Percent(SchemaValidRate) + " (" +
                           SchemaValidCalls + "/" + TotalCalls + ") |");
        builder.AppendLine("| Dry-run fidelity | " + Percent(DryRunFidelityRate) + " |");
        builder.AppendLine("| Read-back mismatch | " + Percent(ReadBackMismatchRate) + " |");
        builder.AppendLine("| Unauthorized writes | " + UnauthorizedWrites + " |");
        builder.AppendLine("| Latency avg / p95 | " +
                           AverageLatencyMs.ToString("F0", CultureInfo.InvariantCulture) + " / " +
                           P95LatencyMs.ToString("F0", CultureInfo.InvariantCulture) + " ms |");
        builder.AppendLine("| Tokens prompt / completion | " + PromptTokens + " / " +
                           CompletionTokens + " |");
        builder.AppendLine("| Threshold gate | " +
                           (MeetsThresholds ? "PASS" : "FAIL: " + string.Join("; ", ThresholdViolations)) +
                           " |");
        builder.AppendLine();

        var failures = results.Where(result => !result.Passed).ToList();
        builder.AppendLine("## Failures (" + failures.Count + ")");
        builder.AppendLine();
        if (failures.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var failure in failures)
            {
                builder.AppendLine("### " + failure.Id + " — " + failure.Kind);
                builder.AppendLine();
                builder.AppendLine("- Reason: " + (failure.Failure ?? "unknown"));
                builder.AppendLine("- Calls: " +
                                   (failure.Calls.Count == 0
                                       ? "(none)"
                                       : string.Join(", ", failure.Calls.Select(call =>
                                           call.ToolName + (call.DryRun ? "(dry)" : string.Empty)))));
                if (!string.IsNullOrWhiteSpace(failure.Answer))
                    builder.AppendLine("- Answer: " + Trim(failure.Answer!, 500));
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string Percent(double value) =>
        (value * 100).ToString("F1", CultureInfo.InvariantCulture) + " %";

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;

        var index = (int)System.Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[System.Math.Max(0, System.Math.Min(sorted.Count - 1, index))];
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max) + "…";
}
