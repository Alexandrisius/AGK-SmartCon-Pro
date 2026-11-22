using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools.Read;

/// <summary>
/// Read layer over the existing agent.db tables (issue #465, H2.6b): the
/// «registry of known models» — unique document_path from sessions and
/// change_sets (jobs are attached through the document_id → path mapping,
/// the jobs table itself has no document_path column). Answers «в какой модели
/// что делали» without touching any .rvt file and without schema changes: it
/// reads the columns the stores already write.
/// </summary>
public sealed class ListKnownModelsTool : IAgentTool
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    private readonly AgentDatabase _database;

    public ListKnownModelsTool(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_known_models",
        Description =
            "Lists models this computer's agent has worked with, from the local agent " +
            "database: path, last activity, session/change-set/job counts and the last " +
            "change summary. Use it to answer «в какой модели делали X» or to pick the " +
            "path for open_model_background when the user refers to a model by what was " +
            "done in it. Optional query filters by substring (path, title, operation, " +
            "description). Read-only: nothing is written to Revit documents.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Category = "Read",
        TimeoutMs = 30000,
        InputSchemaJson =
            @"{""type"":""object"",""properties"":{" +
            @"""query"":{""type"":""string"",""description"":""Optional substring filter: model path, title, last change operation or description""}," +
            @"""limit"":{""type"":""integer"",""minimum"":1,""maximum"":100,""default"":25}}," +
            @"""additionalProperties"":false}"
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var query = GetString(args, "query");
        var limit = Math.Max(1, Math.Min(GetInt(args, "limit", DefaultLimit), MaxLimit));

        List<KnownModelRow> models;
        int jobsWithoutPath;
        try
        {
            var queried = await Task.Run(QueryModels, ct).ConfigureAwait(false);
            models = queried.Models;
            jobsWithoutPath = queried.JobsWithoutPath;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "list_known_models query failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: the agent database is unavailable; restart SmartCon or check " +
                "%APPDATA%\\SmartCon\\Agent\\agent.db]");
            return ToolResult.Fail(
                "tool_failed",
                ex.GetType().Name + ": " + ex.Message);
        }

        var openPaths = await TryCollectOpenPathsAsync(context, ct).ConfigureAwait(false);

        IEnumerable<KnownModelRow> filtered = models;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(row => MatchesQuery(row, query!));
        }

        var page = filtered
            .OrderByDescending(row => row.LastActivity, StringComparer.Ordinal)
            .Take(limit)
            .Select(row => Project(row, openPaths))
            .ToList();

        var payload = new
        {
            total = models.Count,
            returned = page.Count,
            truncated = models.Count > page.Count,
            jobsWithoutPath,
            hint = "Pass a model path to open_model_background to inspect it in the " +
                   "background; close it with close_model when done.",
            models = page
        };

        return ToolResult.Ok(
            JsonSerializer.SerializeToElement(payload),
            "Listed " + page.Count + " of " + models.Count + " known model(s)." +
            (openPaths is null
                ? " currentlyOpen is unavailable in this context (no Revit session)."
                : string.Empty));
    }

    private (List<KnownModelRow> Models, int JobsWithoutPath) QueryModels()
    {
        var byPath = new Dictionary<string, KnownModelRow>(StringComparer.OrdinalIgnoreCase);

        using var connection = _database.CreateConnection();
        connection.Open();

        ReadSessions(connection, byPath);
        ReadChangeSets(connection, byPath);
        var pathByDocumentId = BuildDocumentIdToPathMap(connection, byPath);
        var jobsWithoutPath = ReadJobs(connection, byPath, pathByDocumentId);

        return (byPath.Values.ToList(), jobsWithoutPath);
    }

    private static void ReadSessions(
        SqliteConnection connection,
        Dictionary<string, KnownModelRow> byPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT document_path, COUNT(*) AS session_count, MAX(updated_at) AS last_activity,
       MAX(created_at) AS last_created
FROM sessions
WHERE document_path IS NOT NULL AND document_path <> ''
GROUP BY document_path;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            var row = GetOrAdd(byPath, path);
            row.Sessions = (int)reader.GetInt64(1);
            ConsiderLastActivity(row, reader.IsDBNull(2) ? null : reader.GetString(2));
            ConsiderLastActivity(row, reader.IsDBNull(3) ? null : reader.GetString(3));
        }

        // Latest session title per path (ties resolved by first-read order).
        using var titleCommand = connection.CreateCommand();
        titleCommand.CommandText = @"
SELECT s.document_path, s.title
FROM sessions s
JOIN (
    SELECT document_path, MAX(updated_at) AS m
    FROM sessions
    WHERE document_path IS NOT NULL AND document_path <> ''
    GROUP BY document_path
) t ON t.document_path = s.document_path AND t.m = s.updated_at
WHERE s.document_path IS NOT NULL AND s.document_path <> ''
ORDER BY s.updated_at;";

        using var titleReader = titleCommand.ExecuteReader();
        while (titleReader.Read())
        {
            var row = GetOrAdd(byPath, titleReader.GetString(0));
            if (string.IsNullOrEmpty(row.Title) && !titleReader.IsDBNull(1))
                row.Title = titleReader.GetString(1);
        }
    }

    private static void ReadChangeSets(
        SqliteConnection connection,
        Dictionary<string, KnownModelRow> byPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT document_path, COUNT(*) AS change_set_count,
       MAX(COALESCE(applied_at, created_at)) AS last_change_at
FROM change_sets
WHERE document_path IS NOT NULL AND document_path <> ''
GROUP BY document_path;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = GetOrAdd(byPath, reader.GetString(0));
            row.ChangeSets = (int)reader.GetInt64(1);
            if (!reader.IsDBNull(2))
                ConsiderLastActivity(row, reader.GetString(2));
        }

        using var lastCommand = connection.CreateCommand();
        lastCommand.CommandText = @"
SELECT cs.document_path, cs.operation, cs.description, cs.author
FROM change_sets cs
JOIN (
    SELECT document_path, MAX(COALESCE(applied_at, created_at)) AS m
    FROM change_sets
    WHERE document_path IS NOT NULL AND document_path <> ''
    GROUP BY document_path
) t ON t.document_path = cs.document_path AND t.m = COALESCE(cs.applied_at, cs.created_at)
WHERE cs.document_path IS NOT NULL AND cs.document_path <> ''
ORDER BY cs.created_at;";

        using var lastReader = lastCommand.ExecuteReader();
        while (lastReader.Read())
        {
            var row = GetOrAdd(byPath, lastReader.GetString(0));
            if (string.IsNullOrEmpty(row.LastChangeOperation) && !lastReader.IsDBNull(1))
                row.LastChangeOperation = lastReader.GetString(1);
            if (string.IsNullOrEmpty(row.LastChangeDescription) && !lastReader.IsDBNull(2))
                row.LastChangeDescription = lastReader.GetString(2);
            if (string.IsNullOrEmpty(row.LastAuthor) && !lastReader.IsDBNull(3))
                row.LastAuthor = lastReader.GetString(3);
        }
    }

    /// <summary>
    /// jobs carries document_id only; the id → path mapping is rebuilt from
    /// sessions ∪ change_sets (latest row wins). Job counts for ids without a
    /// known path cannot be represented in a path-keyed listing — they are
    /// reported as jobsWithoutPath instead of being silently dropped.
    /// </summary>
    private static Dictionary<string, string> BuildDocumentIdToPathMap(
        SqliteConnection connection,
        Dictionary<string, KnownModelRow> byPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT document_id, document_path FROM (
    SELECT document_id, document_path, created_at FROM sessions
    WHERE document_path IS NOT NULL AND document_path <> ''
    UNION ALL
    SELECT document_id, document_path, created_at FROM change_sets
    WHERE document_path IS NOT NULL AND document_path <> ''
) ORDER BY created_at;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(0)] = reader.GetString(1);
        }

        return map;
    }

    private static int ReadJobs(
        SqliteConnection connection,
        Dictionary<string, KnownModelRow> byPath,
        Dictionary<string, string> pathByDocumentId)
    {
        var withoutPath = 0;

        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT document_id, COUNT(*) AS job_count,
       MAX(COALESCE(finished_at, updated_at, created_at)) AS last_job_at
FROM jobs
GROUP BY document_id;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var documentId = reader.GetString(0);
            var count = (int)reader.GetInt64(1);
            var lastJobAt = reader.IsDBNull(2) ? null : reader.GetString(2);

            if (!pathByDocumentId.TryGetValue(documentId, out var path))
            {
                withoutPath += count;
                continue;
            }

            var row = GetOrAdd(byPath, path);
            row.Jobs += count;
            ConsiderLastActivity(row, lastJobAt);
        }

        return withoutPath;
    }

    /// <summary>
    /// Open-document paths from the live Revit session, used for the
    /// currentlyOpen flag. Best-effort: a harness without a Revit session
    /// (or a non-Revit callback object) yields null and the flag is simply
    /// absent rather than wrong.
    /// </summary>
    private static async Task<HashSet<string>?> TryCollectOpenPathsAsync(
        AgentToolContext context,
        CancellationToken ct)
    {
        try
        {
            return await context.Revit
                .RaiseAsync(
                    app =>
                    {
                        // No UIApplication cast: RevitAPIUI types must not be
                        // JIT'd in the integration-test host (mixed-mode crash).
                        Autodesk.Revit.ApplicationServices.Application? application =
                            app as Autodesk.Revit.ApplicationServices.Application
                            ?? app.GetType()
                                .GetProperty("Application")
                                ?.GetValue(app) as Autodesk.Revit.ApplicationServices.Application;

                        if (application is null)
                            return null;

                        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (Autodesk.Revit.DB.Document document in application.Documents)
                        {
                            if (!string.IsNullOrEmpty(document.PathName))
                                paths.Add(document.PathName);
                        }

                        return paths;
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                "list_known_models could not read open documents: " +
                ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private static object Project(KnownModelRow row, HashSet<string>? openPaths) => new
    {
        path = row.Path,
        fileName = Path.GetFileName(row.Path),
        title = string.IsNullOrEmpty(row.Title) ? null : row.Title,
        lastActivity = row.LastActivity,
        sessions = row.Sessions,
        changeSets = row.ChangeSets,
        jobs = row.Jobs,
        lastChange = string.IsNullOrEmpty(row.LastChangeOperation)
            ? null
            : new
            {
                operation = row.LastChangeOperation,
                description = row.LastChangeDescription,
                author = row.LastAuthor
            },
        currentlyOpen = openPaths?.Contains(row.Path)
    };

    private static bool MatchesQuery(KnownModelRow row, string query)
    {
        return Contains(row.Path, query)
               || Contains(row.Title, query)
               || Contains(row.LastChangeOperation, query)
               || Contains(row.LastChangeDescription, query);

        static bool Contains(string? text, string fragment) =>
            !string.IsNullOrEmpty(text)
            && text!.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static KnownModelRow GetOrAdd(
        Dictionary<string, KnownModelRow> byPath,
        string path)
    {
        if (!byPath.TryGetValue(path, out var row))
        {
            row = new KnownModelRow { Path = path };
            byPath.Add(path, row);
        }

        return row;
    }

    /// <summary>Dates are stored as UTC round-trip ("O") strings — the ordinal
    /// maximum equals the chronological one.</summary>
    private static void ConsiderLastActivity(KnownModelRow row, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return;

        if (string.IsNullOrEmpty(row.LastActivity)
            || string.Compare(candidate, row.LastActivity, StringComparison.Ordinal) > 0)
        {
            row.LastActivity = candidate;
        }
    }

    private sealed class KnownModelRow
    {
        public string Path { get; set; } = string.Empty;

        public string? Title { get; set; }

        public string? LastActivity { get; set; }

        public int Sessions { get; set; }

        public int ChangeSets { get; set; }

        public int Jobs { get; set; }

        public string? LastChangeOperation { get; set; }

        public string? LastChangeDescription { get; set; }

        public string? LastAuthor { get; set; }
    }

    private static string? GetString(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static int GetInt(JsonElement args, string property, int fallback)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
