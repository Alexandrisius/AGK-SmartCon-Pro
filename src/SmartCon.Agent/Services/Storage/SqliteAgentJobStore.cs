using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

/// <summary>
/// SQLite implementation of <see cref="IJobStore"/>. Header operations live here;
/// units, items, batches and events live in <c>SqliteAgentJobStore.Items.cs</c>.
/// Single writer, DELETE journal, no WAL (I-14).
/// </summary>
public sealed partial class SqliteAgentJobStore : IJobStore
{
    internal static readonly JsonSerializerOptions SerializerOptions = JobJson.Options;

    private const string ActiveStatesSql = "(0, 1, 2, 3, 7, 9)";

    private readonly AgentDatabase _database;

    public SqliteAgentJobStore(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task<JobRecord> CreateAsync(JobSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (spec is null) throw new ArgumentNullException(nameof(spec));

        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var initialState = spec.Mode == JobMode.Preview ? JobState.PreviewRunning : JobState.Running;
        var specJson = JsonSerializer.Serialize(spec, SerializerOptions);

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO jobs(
    id, document_id, action, mode, spec_json, goal, conversation_id,
    state, cancel_requested, preview_digest, created_at, updated_at)
VALUES(
    @id, @documentId, @action, @mode, @specJson, @goal, @conversationId,
    @state, 0, @previewDigest, @createdAt, @updatedAt);";
                command.Parameters.AddWithValue("@id", jobId.ToString("D"));
                command.Parameters.AddWithValue("@documentId", spec.DocumentId);
                command.Parameters.AddWithValue("@action", spec.Action);
                command.Parameters.AddWithValue("@mode", (int)spec.Mode);
                command.Parameters.AddWithValue("@specJson", specJson);
                command.Parameters.AddWithValue("@goal", (object?)spec.Goal ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "@conversationId",
                    spec.ConversationId?.ToString("D") ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@state", (int)initialState);
                command.Parameters.AddWithValue(
                    "@previewDigest",
                    (object?)spec.PreviewDigest ?? DBNull.Value);
                command.Parameters.AddWithValue("@createdAt", FormatTime(now));
                command.Parameters.AddWithValue("@updatedAt", FormatTime(now));
                command.ExecuteNonQuery();
            }

            AppendEventCore(connection, transaction, jobId, now, JobEventTypes.Created, null);
            transaction.Commit();
        }

        return Task.FromResult(new JobRecord
        {
            JobId = jobId,
            Spec = spec,
            State = initialState,
            CreatedAt = now,
            UpdatedAt = now,
            StartedAt = initialState == JobState.Running ? now : null
        });
    }

    public Task<JobRecord?> GetAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            return Task.FromResult(ReadJob(connection, jobId.ToString("D")));
        }
    }

    public Task<JobRecord?> GetActiveAsync(string documentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id FROM jobs
WHERE document_id = @documentId AND state IN " + ActiveStatesSql + @"
ORDER BY created_at DESC
LIMIT 1;";
            command.Parameters.AddWithValue("@documentId", documentId);
            var scalar = command.ExecuteScalar();
            if (scalar is not string id)
                return Task.FromResult<JobRecord?>(null);

            return Task.FromResult(ReadJob(connection, id));
        }
    }

    public Task<JobRecord?> GetByPreviewDigestAsync(string documentId, string digest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id FROM jobs
WHERE document_id = @documentId AND preview_digest = @digest
ORDER BY created_at DESC
LIMIT 1;";
            command.Parameters.AddWithValue("@documentId", documentId);
            command.Parameters.AddWithValue("@digest", digest);
            var scalar = command.ExecuteScalar();
            if (scalar is not string id)
                return Task.FromResult<JobRecord?>(null);

            return Task.FromResult(ReadJob(connection, id));
        }
    }

    public Task<IReadOnlyList<JobRecord>> ListRecentAsync(string documentId, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (limit <= 0) limit = 10;

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id FROM jobs
WHERE document_id = @documentId
ORDER BY created_at DESC
LIMIT @limit;";
            command.Parameters.AddWithValue("@documentId", documentId);
            command.Parameters.AddWithValue("@limit", limit);

            var ids = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetString(0));
            reader.Close();

            var jobs = new List<JobRecord>(ids.Count);
            foreach (var id in ids)
            {
                var job = ReadJob(connection, id);
                if (job is not null)
                    jobs.Add(job);
            }

            return Task.FromResult<IReadOnlyList<JobRecord>>(jobs);
        }
    }

    public Task<JobError?> TransitionAsync(
        Guid jobId,
        JobState expectedFrom,
        JobState to,
        string? errorCode,
        string? errorDetail,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var validation = JobStateMachine.Validate(expectedFrom, to);
        if (validation is not null)
            return Task.FromResult<JobError?>(validation);

        var now = DateTime.UtcNow;
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            int affected;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE jobs SET
    state = @to,
    error_code = @errorCode,
    error_detail = @errorDetail,
    updated_at = @updatedAt,
    started_at = CASE WHEN @to = 3 AND started_at IS NULL THEN @updatedAt ELSE started_at END,
    finished_at = CASE WHEN @terminal = 1 THEN @updatedAt ELSE finished_at END
WHERE id = @id AND state = @from;";
                command.Parameters.AddWithValue("@to", (int)to);
                command.Parameters.AddWithValue("@errorCode", (object?)errorCode ?? DBNull.Value);
                command.Parameters.AddWithValue("@errorDetail", (object?)errorDetail ?? DBNull.Value);
                command.Parameters.AddWithValue("@updatedAt", FormatTime(now));
                command.Parameters.AddWithValue("@terminal", JobStateMachine.IsTerminal(to) ? 1 : 0);
                command.Parameters.AddWithValue("@id", jobId.ToString("D"));
                command.Parameters.AddWithValue("@from", (int)expectedFrom);
                affected = command.ExecuteNonQuery();
            }

            if (affected == 0)
            {
                transaction.Rollback();
                return Task.FromResult(ReadTransitionFailure(connection, jobId, expectedFrom, to));
            }

            AppendEventCore(
                connection,
                transaction,
                jobId,
                now,
                "state_" + to.ToString().ToLowerInvariant(),
                null);
            transaction.Commit();
        }

        return Task.FromResult<JobError?>(null);
    }

    public Task UpdateSpecAsync(Guid jobId, JobSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (spec is null) throw new ArgumentNullException(nameof(spec));

        var specJson = JsonSerializer.Serialize(spec, SerializerOptions);
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE jobs SET
    spec_json = @specJson,
    mode = @mode,
    goal = @goal,
    updated_at = @updatedAt
WHERE id = @id;";
            command.Parameters.AddWithValue("@specJson", specJson);
            command.Parameters.AddWithValue("@mode", (int)spec.Mode);
            command.Parameters.AddWithValue("@goal", (object?)spec.Goal ?? DBNull.Value);
            command.Parameters.AddWithValue("@updatedAt", FormatTime(DateTime.UtcNow));
            command.Parameters.AddWithValue("@id", jobId.ToString("D"));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task UpdatePreviewAsync(Guid jobId, string previewJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var digest = TryReadDigest(previewJson);
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE jobs SET
    preview_json = @previewJson,
    preview_digest = COALESCE(@digest, preview_digest),
    updated_at = @updatedAt
WHERE id = @id;";
            command.Parameters.AddWithValue("@previewJson", previewJson);
            command.Parameters.AddWithValue("@digest", (object?)digest ?? DBNull.Value);
            command.Parameters.AddWithValue("@updatedAt", FormatTime(DateTime.UtcNow));
            command.Parameters.AddWithValue("@id", jobId.ToString("D"));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task UpdateTotalsAsync(Guid jobId, string totalsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET totals_json = @totals, updated_at = @updatedAt WHERE id = @id;";
            command.Parameters.AddWithValue("@totals", totalsJson);
            command.Parameters.AddWithValue("@updatedAt", FormatTime(DateTime.UtcNow));
            command.Parameters.AddWithValue("@id", jobId.ToString("D"));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task UpdateCursorAsync(Guid jobId, int lastSavedCursor, DateTime savedAtUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE jobs SET
    last_saved_cursor = @cursor,
    last_saved_at = @savedAt,
    updated_at = @savedAt
WHERE id = @id;";
                command.Parameters.AddWithValue("@cursor", lastSavedCursor);
                command.Parameters.AddWithValue("@savedAt", FormatTime(savedAtUtc));
                command.Parameters.AddWithValue("@id", jobId.ToString("D"));
                command.ExecuteNonQuery();
            }

            AppendEventCore(
                connection,
                transaction,
                jobId,
                savedAtUtc,
                JobEventTypes.Saved,
                "{\"cursor\":" + lastSavedCursor.ToString(CultureInfo.InvariantCulture) + "}");
            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task SetCancelRequestedAsync(Guid jobId, bool requested, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE jobs SET cancel_requested = @requested, updated_at = @updatedAt WHERE id = @id;";
                command.Parameters.AddWithValue("@requested", requested ? 1 : 0);
                command.Parameters.AddWithValue("@updatedAt", FormatTime(DateTime.UtcNow));
                command.Parameters.AddWithValue("@id", jobId.ToString("D"));
                command.ExecuteNonQuery();
            }

            if (requested)
            {
                AppendEventCore(
                    connection,
                    transaction,
                    jobId,
                    DateTime.UtcNow,
                    JobEventTypes.CancelRequested,
                    null);
            }

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task RenewLeaseAsync(Guid jobId, DateTime leaseUntilUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET lease_until = @lease WHERE id = @id;";
            command.Parameters.AddWithValue("@lease", FormatTime(leaseUntilUtc));
            command.Parameters.AddWithValue("@id", jobId.ToString("D"));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task MarkStartedAsync(Guid jobId, DateTime startedAtUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE jobs SET
    started_at = COALESCE(started_at, @startedAt),
    updated_at = @startedAt
WHERE id = @id;";
            command.Parameters.AddWithValue("@startedAt", FormatTime(startedAtUtc));
            command.Parameters.AddWithValue("@id", jobId.ToString("D"));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task MarkFinishedAsync(
        Guid jobId,
        JobState finalState,
        string? errorCode,
        string? errorDetail,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            // Terminal states are final: the write is allowed when the row already
            // holds the target state (finalizing a transition) or when the row is
            // still non-terminal; a racing slice can never rewrite another terminal.
            command.CommandText = @"
UPDATE jobs SET
    state = @state,
    error_code = @errorCode,
    error_detail = @errorDetail,
    finished_at = @finishedAt,
    updated_at = @finishedAt
WHERE id = @id AND (state = @state OR state NOT IN (4, 5, 6, 8, 10, 11));";
            command.Parameters.AddWithValue("@state", (int)finalState);
            command.Parameters.AddWithValue("@errorCode", (object?)errorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@errorDetail", (object?)errorDetail ?? DBNull.Value);
            command.Parameters.AddWithValue("@finishedAt", FormatTime(now));
            command.Parameters.AddWithValue("@id", jobId.ToString("D"));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    internal static string FormatTime(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static DateTime ParseTime(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime();

    internal static DateTime? ParseTimeOrNull(object? value) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? ParseTime(text) : null;

    private static string? TryReadDigest(string previewJson)
    {
        try
        {
            using var document = JsonDocument.Parse(previewJson);
            return document.RootElement.TryGetProperty("digest", out var digest)
                ? digest.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JobError? ReadTransitionFailure(
        SqliteConnection connection,
        Guid jobId,
        JobState expectedFrom,
        JobState to)
    {
        var current = ReadState(connection, jobId.ToString("D"));
        if (current is null)
        {
            return new JobError(
                JobErrorCodes.JobNotFound,
                "Job '" + jobId.ToString("D") + "' was not found",
                "call get_job_status to list recent jobs, or start a new job");
        }

        return JobErrors.InvalidState(
            "expected state '" + expectedFrom + "' but the stored state is '" + current + "'",
            "read the current job status and retry the intended transition");
    }

    private static JobState? ReadState(SqliteConnection connection, string jobId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM jobs WHERE id = @id;";
        command.Parameters.AddWithValue("@id", jobId);
        var scalar = command.ExecuteScalar();
        if (scalar is null || scalar is DBNull)
            return null;

        return (JobState)Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static void AppendEventCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        DateTime at,
        string type,
        string? payloadJson)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO job_events(job_id, ts, type, payload_json)
VALUES(@jobId, @ts, @type, @payloadJson);";
        command.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("@ts", FormatTime(at));
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@payloadJson", (object?)payloadJson ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static JobRecord? ReadJob(SqliteConnection connection, string jobId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, spec_json, state, cancel_requested, preview_json, error_code, error_detail,
       last_saved_cursor, last_saved_at, created_at, updated_at, started_at, finished_at,
       totals_json, lease_until
FROM jobs WHERE id = @id;";
        command.Parameters.AddWithValue("@id", jobId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var specJson = reader.GetString(1);
        JobSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<JobSpec>(specJson, SerializerOptions);
        }
        catch (JsonException ex)
        {
            SmartConLogger.Error(
                "Agent job " + jobId + " has an unreadable spec_json: " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: cancel the job (cancel_job) and start a new one]");
            return null;
        }

        if (spec is null)
            return null;

        return new JobRecord
        {
            JobId = Guid.Parse(reader.GetString(0)),
            Spec = spec,
            State = (JobState)reader.GetInt32(2),
            CancelRequested = reader.GetInt32(3) != 0,
            PreviewJson = reader.IsDBNull(4) ? null : reader.GetString(4),
            ErrorCode = reader.IsDBNull(5) ? null : reader.GetString(5),
            ErrorDetail = reader.IsDBNull(6) ? null : reader.GetString(6),
            LastSavedCursor = reader.GetInt32(7),
            LastSavedAt = ParseTimeOrNull(reader.IsDBNull(8) ? null : reader.GetString(8)),
            CreatedAt = ParseTime(reader.GetString(9)),
            UpdatedAt = ParseTime(reader.GetString(10)),
            StartedAt = ParseTimeOrNull(reader.IsDBNull(11) ? null : reader.GetString(11)),
            FinishedAt = ParseTimeOrNull(reader.IsDBNull(12) ? null : reader.GetString(12)),
            TotalsJson = reader.IsDBNull(13) ? null : reader.GetString(13),
            LeaseUntil = ParseTimeOrNull(reader.IsDBNull(14) ? null : reader.GetString(14))
        };
    }
}