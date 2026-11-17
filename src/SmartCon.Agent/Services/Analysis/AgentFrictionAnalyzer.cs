using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;

namespace SmartCon.Agent.Services.Analysis;

public sealed class ToolFriction
{
    public required string ToolName { get; init; }

    public int Calls { get; init; }

    public int Failures { get; init; }

    public double AverageDurationMs { get; init; }

    public double FailureRate => Calls == 0 ? 0 : (double)Failures / Calls;
}

public sealed class ErrorCodeCount
{
    public required string Code { get; init; }

    public int Count { get; init; }
}

public sealed class AgentFrictionReport
{
    public int TotalCalls { get; init; }

    public int FailedCalls { get; init; }

    public int Sessions { get; init; }

    public double AverageDurationMs { get; init; }

    public double AverageCallsPerSession { get; init; }

    public int TruncatedResults { get; init; }

    public int InvalidArguments { get; init; }

    public int DocumentChanged { get; init; }

    public int FeedbackEntries { get; init; }

    public double AverageFeedbackRating { get; init; }

    public IReadOnlyList<ToolFriction> Tools { get; init; } = Array.Empty<ToolFriction>();

    public IReadOnlyList<ErrorCodeCount> ErrorCodes { get; init; } = Array.Empty<ErrorCodeCount>();

    public IReadOnlyList<string> Recommendations { get; init; } = Array.Empty<string>();

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SmartCon Agent — friction report");
        builder.AppendLine();
        builder.AppendLine("| Metric | Value |");
        builder.AppendLine("|---|---|");
        builder.AppendLine("| Tool calls | " + TotalCalls + " |");
        builder.AppendLine("| Failed calls | " + FailedCalls + " |");
        builder.AppendLine("| Sessions | " + Sessions + " |");
        builder.AppendLine("| Avg calls per session | " +
                           AverageCallsPerSession.ToString("F2", CultureInfo.InvariantCulture) + " |");
        builder.AppendLine("| Avg duration | " +
                           AverageDurationMs.ToString("F0", CultureInfo.InvariantCulture) + " ms |");
        builder.AppendLine("| invalid_arguments | " + InvalidArguments + " |");
        builder.AppendLine("| document_changed | " + DocumentChanged + " |");
        builder.AppendLine("| Truncated results | " + TruncatedResults + " |");
        builder.AppendLine("| Feedback entries | " + FeedbackEntries + " |");
        builder.AppendLine("| Avg feedback rating | " +
                           AverageFeedbackRating.ToString("F2", CultureInfo.InvariantCulture) + " |");
        builder.AppendLine();

        if (Tools.Count > 0)
        {
            builder.AppendLine("## Tools");
            builder.AppendLine();
            builder.AppendLine("| Tool | Calls | Failures | Avg ms |");
            builder.AppendLine("|---|---|---|---|");
            foreach (var tool in Tools)
            {
                builder.AppendLine("| " + tool.ToolName + " | " + tool.Calls + " | " +
                                   tool.Failures + " | " +
                                   tool.AverageDurationMs.ToString("F0", CultureInfo.InvariantCulture) +
                                   " |");
            }

            builder.AppendLine();
        }

        if (ErrorCodes.Count > 0)
        {
            builder.AppendLine("## Error codes");
            builder.AppendLine();
            foreach (var code in ErrorCodes)
                builder.AppendLine("- `" + code.Code + "` — " + code.Count);
            builder.AppendLine();
        }

        if (Recommendations.Count > 0)
        {
            builder.AppendLine("## Recommendations");
            builder.AppendLine();
            foreach (var recommendation in Recommendations)
                builder.AppendLine("- " + recommendation);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Reads agent.db and reports where the model struggles with the tooling
/// (mvp-plan Phase 3 "AgentFrictionAnalyzer").
/// </summary>
public sealed class AgentFrictionAnalyzer
{
    private readonly AgentDatabase _database;

    public AgentFrictionAnalyzer(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public AgentFrictionReport Analyze(int days = 30, string? documentId = null)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, days))
            .ToString("O", CultureInfo.InvariantCulture);

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();

            var totals = ReadTotals(connection, since, documentId);
            var tools = ReadTools(connection, since, documentId);
            var errors = ReadErrors(connection, since, documentId);
            var feedback = ReadFeedback(connection, since, documentId);

            var report = new AgentFrictionReport
            {
                TotalCalls = totals.Calls,
                FailedCalls = totals.Failures,
                Sessions = totals.Sessions,
                AverageDurationMs = totals.AverageDurationMs,
                AverageCallsPerSession = totals.Sessions == 0
                    ? 0
                    : (double)totals.Calls / totals.Sessions,
                TruncatedResults = totals.Truncated,
                InvalidArguments = errors
                    .Where(code => code.Code == "invalid_arguments")
                    .Sum(code => code.Count),
                DocumentChanged = errors
                    .Where(code => code.Code == "document_changed")
                    .Sum(code => code.Count),
                FeedbackEntries = feedback.Count,
                AverageFeedbackRating = feedback.Average,
                Tools = tools,
                ErrorCodes = errors
            };

            return WithRecommendations(report);
        }
    }

    private static (int Calls, int Failures, int Sessions, double AverageDurationMs, int Truncated)
        ReadTotals(SqliteConnection connection, string since, string? documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*),
       COALESCE(SUM(CASE WHEN status <> 'ok' THEN 1 ELSE 0 END), 0),
       COUNT(DISTINCT session_id),
       COALESCE(AVG(duration_ms), 0),
       COALESCE(SUM(CASE WHEN result_json LIKE '%""truncated"":true%' THEN 1 ELSE 0 END), 0)
FROM tool_calls
WHERE created_at >= @since;";
        command.Parameters.AddWithValue("@since", since);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return (0, 0, 0, 0, 0);

        return (
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetDouble(3),
            reader.GetInt32(4));
    }

    private static IReadOnlyList<ToolFriction> ReadTools(
        SqliteConnection connection,
        string since,
        string? documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT tool_name,
       COUNT(*),
       COALESCE(SUM(CASE WHEN status <> 'ok' THEN 1 ELSE 0 END), 0),
       COALESCE(AVG(duration_ms), 0)
FROM tool_calls
WHERE created_at >= @since
GROUP BY tool_name
ORDER BY COUNT(*) DESC;";
        command.Parameters.AddWithValue("@since", since);

        var result = new List<ToolFriction>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ToolFriction
            {
                ToolName = reader.GetString(0),
                Calls = reader.GetInt32(1),
                Failures = reader.GetInt32(2),
                AverageDurationMs = reader.GetDouble(3)
            });
        }

        return result;
    }

    private static IReadOnlyList<ErrorCodeCount> ReadErrors(
        SqliteConnection connection,
        string since,
        string? documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT error, COUNT(*)
FROM tool_calls
WHERE created_at >= @since AND error IS NOT NULL AND error <> ''
GROUP BY error
ORDER BY COUNT(*) DESC;";
        command.Parameters.AddWithValue("@since", since);

        var result = new List<ErrorCodeCount>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ErrorCodeCount
            {
                Code = reader.GetString(0),
                Count = reader.GetInt32(1)
            });
        }

        return result;
    }

    private static (int Count, double Average) ReadFeedback(
        SqliteConnection connection,
        string since,
        string? documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COUNT(*), COALESCE(AVG(rating), 0)
FROM agent_feedback
WHERE created_at >= @since;";
        command.Parameters.AddWithValue("@since", since);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return (0, 0);

        return (reader.GetInt32(0), reader.GetDouble(1));
    }

    private static AgentFrictionReport WithRecommendations(AgentFrictionReport report)
    {
        var recommendations = new List<string>();

        if (report.InvalidArguments > 0)
        {
            recommendations.Add(
                "invalid_arguments = " + report.InvalidArguments +
                ": tighten tool schemas/descriptions for the tools below and add argument aliases.");
        }

        if (report.TruncatedResults > 0)
        {
            recommendations.Add(
                "truncated results = " + report.TruncatedResults +
                ": lower default limits or add projections (fields/summaryOnly) for the heaviest tools.");
        }

        if (report.DocumentChanged > 0)
        {
            recommendations.Add(
                "document_changed = " + report.DocumentChanged +
                ": explain in the prompt that a new request must be started after switching documents.");
        }

        if (report.AverageCallsPerSession > 8)
        {
            recommendations.Add(
                "Average calls per session is high (" +
                report.AverageCallsPerSession.ToString("F1", CultureInfo.InvariantCulture) +
                "): improve the system prompt workflow hints or add a combined tool for the "
                + "most repeated sequence.");
        }

        var worstTool = report.Tools
            .Where(tool => tool.Calls >= 3 && tool.FailureRate > 0.2)
            .OrderByDescending(tool => tool.FailureRate)
            .FirstOrDefault();

        if (worstTool is not null)
        {
            recommendations.Add(
                "Tool '" + worstTool.ToolName + "' fails in " +
                (worstTool.FailureRate * 100).ToString("F0", CultureInfo.InvariantCulture) +
                "% of calls — review its validation messages and examples.");
        }

        if (report.FeedbackEntries > 0 && report.AverageFeedbackRating < 3.5)
        {
            recommendations.Add(
                "Model-reported rating is low (" +
                report.AverageFeedbackRating.ToString("F1", CultureInfo.InvariantCulture) +
                "): read agent_feedback for concrete suggestions.");
        }

        if (recommendations.Count == 0)
            recommendations.Add("No friction detected — tooling behaves as expected.");

        return new AgentFrictionReport
        {
            TotalCalls = report.TotalCalls,
            FailedCalls = report.FailedCalls,
            Sessions = report.Sessions,
            AverageDurationMs = report.AverageDurationMs,
            AverageCallsPerSession = report.AverageCallsPerSession,
            TruncatedResults = report.TruncatedResults,
            InvalidArguments = report.InvalidArguments,
            DocumentChanged = report.DocumentChanged,
            FeedbackEntries = report.FeedbackEntries,
            AverageFeedbackRating = report.AverageFeedbackRating,
            Tools = report.Tools,
            ErrorCodes = report.ErrorCodes,
            Recommendations = recommendations
        };
    }
}
