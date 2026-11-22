using System.Text.Json;
using System.Text;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Transcript markdown of one production-loop benchmark run: header (provider,
/// model, reasoning, budgets, Revit, catalog, base), the verbatim user prompt,
/// per-round assistant text and tool calls, harness notes, final answer,
/// verdicts, state read-back and the summary. Partial of
/// <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private static string BuildProductionTranscript(
        int caseNumber,
        string title,
        string caseText,
        IReadOnlyList<string> seedSummary,
        string provider,
        string model,
        string reasoning,
        int maxOutputTokens,
        int maxRounds,
        int maxSeconds,
        int catalogSize,
        string revitVersion,
        Dictionary<int, string> assistantTextByRound,
        List<BenchCallArtifact> calls,
        IReadOnlyList<string> notes,
        string? answer,
        CaseVerdict verdict,
        CaseVerdict stateVerdict,
        string stateReadBack,
        int llmRounds,
        List<BenchCall> allCalls,
        int promptTokens,
        int completionTokens,
        DateTime startedUtc,
        DateTime endedUtc,
        string stopReason,
        string? error)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SmartCon production-loop benchmark, кейс " + caseNumber + " — " + title);
        builder.AppendLine();
        builder.AppendLine("- Harness: production loop (AgentOrchestrator via ProductionAgentHost)");
        builder.AppendLine("- Provider: " + provider);
        builder.AppendLine("- Model: " + model);
        builder.AppendLine("- Reasoning: " + reasoning);
        builder.AppendLine("- Budgets: maxOutputTokens " + maxOutputTokens +
                          ", maxRounds " + maxRounds + ", maxSeconds " + maxSeconds);
        builder.AppendLine("- Revit: " + revitVersion);
        builder.AppendLine("- Tool catalog: " + catalogSize + " definitions");
        builder.AppendLine("- Base: " + BenchRunArtifactWriter.BaseLabel());
        builder.AppendLine("- Started: " + startedUtc.ToString("O"));
        builder.AppendLine("- Seeded model:");
        foreach (var line in seedSummary)
            builder.AppendLine("  - " + line);
        builder.AppendLine();
        builder.AppendLine("## User prompt (дословно; единственное сообщение пользователя)");
        builder.AppendLine();
        builder.AppendLine("> " + caseText.Replace("\n", "\n> "));
        builder.AppendLine();
        builder.AppendLine("## Dialog");
        builder.AppendLine();

        foreach (var group in calls.GroupBy(call => call.Round).OrderBy(group => group.Key))
        {
            builder.AppendLine("### Раунд " + group.Key);
            builder.AppendLine();
            if (assistantTextByRound.TryGetValue(group.Key, out var assistantText))
            {
                builder.AppendLine("**Assistant:** " + Shorten(assistantText, 600));
                builder.AppendLine();
            }

            foreach (var call in group)
            {
                builder.AppendLine("- `" + call.Tool + "` " + Shorten(call.Args, 700));
                builder.AppendLine("  → " + (call.Success ? "ok" : "FAIL " + call.ErrorCode) + " " +
                                   Shorten(call.Message, 500) + " (" + call.DurationMs + " ms)");
            }

            builder.AppendLine();
        }

        foreach (var note in notes)
        {
            builder.AppendLine("**Harness:** " + note);
            builder.AppendLine();
        }

        if (error is not null)
        {
            builder.AppendLine("**Error:** " + error);
            builder.AppendLine();
        }

        builder.AppendLine("## Final answer");
        builder.AppendLine();
        builder.AppendLine(answer ?? "(no final answer)");
        builder.AppendLine();
        builder.AppendLine("## Verdict");
        builder.AppendLine();
        builder.AppendLine("**" + verdict.Label + "** (score " +
                          verdict.Score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                          ") — " + verdict.Detail);
        builder.AppendLine();
        builder.AppendLine("State verdict (без стоп-кэпа): **" + stateVerdict.Label + "** (score " +
                           stateVerdict.Score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ")");
        if (verdict.RedFlags.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Red flags:");
            foreach (var flag in verdict.RedFlags)
                builder.AppendLine("- " + flag);
        }

        builder.AppendLine();
        builder.AppendLine("## State read-back");
        builder.AppendLine();
        builder.AppendLine(stateReadBack);
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine("- LLM rounds: " + llmRounds + " (cap " + maxRounds + ")");
        builder.AppendLine("- Tool calls: " + allCalls.Count + " (" +
                          string.Join(" → ", allCalls.Select(call =>
                              call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")"))) + ")");
        builder.AppendLine("- Final answer: " + (string.IsNullOrWhiteSpace(answer) ? "no" : "yes"));
        builder.AppendLine("- Stop reason: " + stopReason);
        builder.AppendLine("- Tokens: prompt " + promptTokens + ", completion " + completionTokens);
        builder.AppendLine("- Duration: " + (endedUtc - startedUtc).TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s");
        builder.AppendLine("- Finished: " + endedUtc.ToString("O"));
        return builder.ToString();
    }

    /// <summary>message из результата тул-вызова (или первые 500 символов JSON).</summary>
    private static string ResultMessage(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // A malformed result falls through to the raw prefix.
        }

        return Shorten(resultJson, 500);
    }
}
