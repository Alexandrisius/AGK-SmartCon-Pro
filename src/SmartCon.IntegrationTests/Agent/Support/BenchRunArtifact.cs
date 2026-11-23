using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartCon.IntegrationTests.Agent.Support;

/// <summary>One tool call of a benchmark run as it lands in the JSON artifact.</summary>
public sealed class BenchCallArtifact
{
    public int Round { get; set; }

    public int Seq { get; set; }

    public string Tool { get; set; } = string.Empty;

    public string Args { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>message из результата вызова (или начало JSON результата).</summary>
    public string Message { get; set; } = string.Empty;

    public long DurationMs { get; set; }
}

/// <summary>Verdict block of a benchmark run (overall + model-state read separately).</summary>
public sealed class BenchVerdictArtifact
{
    public string Label { get; set; } = "FAIL";

    public double Score { get; set; }

    public string Detail { get; set; } = string.Empty;

    public IReadOnlyList<string> RedFlags { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Objective per-run record of one production-loop benchmark case: time, rounds,
/// tool calls, tokens, stop reason and the verdict, so two models on the same
/// case/base can be compared from the JSON alone (transcript kept next to it).
/// </summary>
public sealed class BenchRunArtifact
{
    public int CaseNumber { get; set; }

    public string CaseKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>"synthetic" | "template:&lt;file name&gt;".</summary>
    public string Base { get; set; } = "synthetic";

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string Reasoning { get; set; } = string.Empty;

    public int MaxOutputTokens { get; set; }

    public int MaxRounds { get; set; }

    /// <summary>Wall-clock budget of the case in seconds (SMARTCON_BENCH_MAX_SECONDS).</summary>
    public int MaxSeconds { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime EndedUtc { get; set; }

    public double DurationSeconds { get; set; }

    public int Rounds { get; set; }

    public int ToolCalls { get; set; }

    public Dictionary<string, int> ToolCallsByName { get; set; } = new(StringComparer.Ordinal);

    public int WriteCalls { get; set; }

    public int DryRunCalls { get; set; }

    public Dictionary<string, int> ErrorCallsByCode { get; set; } = new(StringComparer.Ordinal);

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public int ReasoningTokens { get; set; }

    public int CachedTokens { get; set; }

    public string StopReason { get; set; } = "ok";

    /// <summary>
    /// Бюджетный steering был отправлен модели: "rounds" (~80% раундов) или
    /// "time" (~75% времени); null — бюджета хватило. Вердикт не меняет.
    /// </summary>
    public string? SteeringReason { get; set; }

    /// <summary>
    /// Бюджет исчерпан и модель дала финальный ответ отдельным вызовом без
    /// инструментов (см. <see cref="Answer"/>; stopReason = round_budget|time_budget).
    /// </summary>
    public bool BudgetFinalCall { get; set; }

    /// <summary>
    /// Токены просуммированы по всем завершённым LLM-вызовам из token_usage
    /// сессии; false — записи не прочитались, сумма только по вернувшимся turn'ам.
    /// </summary>
    public bool UsageFromStore { get; set; }

    public string? Error { get; set; }

    public BenchVerdictArtifact Verdict { get; set; } = new();

    /// <summary>Verdict of the model state alone (before the stop-reason cap).</summary>
    public BenchVerdictArtifact? StateVerdict { get; set; }

    public string? Answer { get; set; }

    public IReadOnlyList<string> IntermediateAnswers { get; set; } = Array.Empty<string>();

    public List<BenchCallArtifact> Calls { get; set; } = new();

    public int? FirstWriteRound { get; set; }

    public bool VerifiedAfterLastWrite { get; set; }

    public string? TranscriptPath { get; set; }
}

/// <summary>
/// Writes the JSON artifact and the transcript markdown next to it:
/// <c>&lt;root&gt;/&lt;provider&gt;/&lt;model&gt;/&lt;base&gt;/&lt;caseKey&gt;/r&lt;yyyyMMdd-HHmmss&gt;.{json,md}</c>.
/// The root is <c>SMARTCON_BENCH_RESULTS_DIR</c> (absolute) or
/// <c>%LOCALAPPDATA%\Temp\smartcon-bench\results</c> — never <c>Path.GetTempPath()</c>,
/// which inside the Revit test host points at a GUID scratch directory.
/// </summary>
public static class BenchRunArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ResultsRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SMARTCON_BENCH_RESULTS_DIR");
        return !string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured)
            ? configured!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", "smartcon-bench", "results");
    }

    /// <summary>"synthetic" or "template:&lt;file name&gt;" of SMARTCON_BENCH_TEMPLATE.</summary>
    public static string BaseLabel()
    {
        var template = Environment.GetEnvironmentVariable("SMARTCON_BENCH_TEMPLATE");
        return string.IsNullOrWhiteSpace(template)
            ? "synthetic"
            : "template:" + Path.GetFileName(template);
    }

    public static (string JsonPath, string MarkdownPath) Write(
        BenchRunArtifact artifact,
        string transcriptMarkdown)
    {
        var directory = Path.Combine(
            ResultsRoot(),
            Sanitize(artifact.Provider),
            Sanitize(artifact.Model),
            Sanitize(artifact.Base),
            Sanitize(artifact.CaseKey));
        Directory.CreateDirectory(directory);

        var stamp = artifact.StartedUtc.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(directory, "r" + stamp + ".json");
        var markdownPath = Path.Combine(directory, "r" + stamp + ".md");

        File.WriteAllText(markdownPath, transcriptMarkdown, System.Text.Encoding.UTF8);
        artifact.TranscriptPath = markdownPath;
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(artifact, JsonOptions), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.WriteLine("Benchmark artifact (case " + artifact.CaseNumber + "): " + jsonPath);
        Console.WriteLine("Benchmark transcript (case " + artifact.CaseNumber + "): " + markdownPath);
        return (jsonPath, markdownPath);
    }

    /// <summary>
    /// Path of the session database next to a written artifact:
    /// <c>r&lt;stamp&gt;.db</c> beside <c>r&lt;stamp&gt;.json</c>. ProductionAgentHost copies
    /// its temp database there on dispose (SMARTCON_BENCH_KEEP_DB, on by default),
    /// so a live run can be dissected by messages/tool_calls afterwards.
    /// </summary>
    public static string DatabasePathFor(string jsonPath) =>
        Path.Combine(
            Path.GetDirectoryName(jsonPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(jsonPath) + ".db");

    /// <summary>File-system-safe path segment (providers/models are URL-ish names).</summary>
    private static string Sanitize(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = segment.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var safe = new string(chars).TrimEnd('.');
        return safe.Length == 0 ? "_" : safe;
    }
}
