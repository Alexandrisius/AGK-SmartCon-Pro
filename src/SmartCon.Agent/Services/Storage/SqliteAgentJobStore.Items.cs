using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

/// <summary>Units, items, batches and append-only events of <see cref="SqliteAgentJobStore"/>.</summary>
public sealed partial class SqliteAgentJobStore
{
    public Task AddUnitsAsync(Guid jobId, IReadOnlyList<JobUnit> units, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (units is null) throw new ArgumentNullException(nameof(units));
        if (units.Count == 0) return Task.CompletedTask;

        var now = FormatTime(DateTime.UtcNow);
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            foreach (var unit in units)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO job_items(job_id, seq, unit_key, args_json, label, state, attempts, updated_at)
VALUES(@jobId, @seq, @unitKey, @argsJson, @label, @state, 0, @updatedAt);";
                command.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
                command.Parameters.AddWithValue("@seq", unit.Seq);
                command.Parameters.AddWithValue("@unitKey", unit.UnitKey);
                command.Parameters.AddWithValue("@argsJson", unit.ArgsJson);
                command.Parameters.AddWithValue("@label", (object?)unit.Label ?? DBNull.Value);
                command.Parameters.AddWithValue("@state", (int)JobItemState.Pending);
                command.Parameters.AddWithValue("@updatedAt", now);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobItem>> LoadItemsAsync(
        Guid jobId,
        IReadOnlyList<JobItemState> states,
        int afterSeq,
        int limit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (limit <= 0) limit = 100;

        var stateFilter = BuildStateFilter(states);
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT seq, unit_key, args_json, label, state, preview_json, expected_json, actual_json,
       undo_json, error_code, error_detail, attempts, batch_index, updated_at
FROM job_items
WHERE job_id = @jobId AND seq > @afterSeq AND state IN " + stateFilter + @"
ORDER BY seq
LIMIT @limit;";
            command.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
            command.Parameters.AddWithValue("@afterSeq", afterSeq);
            command.Parameters.AddWithValue("@limit", limit);
            return Task.FromResult(ReadItems(command));
        }
    }

    public Task<IReadOnlyList<JobItem>> LoadAllItemsAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT seq, unit_key, args_json, label, state, preview_json, expected_json, actual_json,
       undo_json, error_code, error_detail, attempts, batch_index, updated_at
FROM job_items
WHERE job_id = @jobId
ORDER BY seq;";
            command.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
            return Task.FromResult(ReadItems(command));
        }
    }

    public Task<int> CountItemsAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM job_items WHERE job_id = @jobId;";
            command.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
            var scalar = command.ExecuteScalar();
            return Task.FromResult(scalar is null || scalar is DBNull
                ? 0
                : Convert.ToInt32(scalar, CultureInfo.InvariantCulture));
        }
    }

    public Task UpdateItemsAsync(Guid jobId, IReadOnlyList<JobItemUpdate> updates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (updates is null) throw new ArgumentNullException(nameof(updates));
        if (updates.Count == 0) return Task.CompletedTask;

        var now = FormatTime(DateTime.UtcNow);
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            foreach (var update in updates)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE job_items SET
    state = @state,
    preview_json = COALESCE(@previewJson, preview_json),
    expected_json = COALESCE(@expectedJson, expected_json),
    actual_json = COALESCE(@actualJson, actual_json),
    undo_json = COALESCE(@undoJson, undo_json),
    error_code = @errorCode,
    error_detail = @errorDetail,
    attempts = attempts + @attemptIncrement,
    batch_index = COALESCE(@batchIndex, batch_index),
    updated_at = @updatedAt
WHERE job_id = @jobId AND seq = @seq;";
                command.Parameters.AddWithValue("@state", (int)update.State);
                command.Parameters.AddWithValue("@previewJson", (object?)update.PreviewJson ?? DBNull.Value);
                command.Parameters.AddWithValue("@expectedJson", (object?)update.ExpectedJson ?? DBNull.Value);
                command.Parameters.AddWithValue("@actualJson", (object?)update.ActualJson ?? DBNull.Value);
                command.Parameters.AddWithValue("@undoJson", (object?)update.UndoJson ?? DBNull.Value);
                command.Parameters.AddWithValue("@errorCode", (object?)update.ErrorCode ?? DBNull.Value);
                command.Parameters.AddWithValue("@errorDetail", (object?)update.ErrorDetail ?? DBNull.Value);
                command.Parameters.AddWithValue("@attemptIncrement", update.IncrementAttempts ? 1 : 0);
                command.Parameters.AddWithValue("@batchIndex", (object?)update.BatchIndex ?? DBNull.Value);
                command.Parameters.AddWithValue("@updatedAt", now);
                command.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
                command.Parameters.AddWithValue("@seq", update.Seq);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task ResetItemsAsync(Guid jobId, IReadOnlyList<int> seqs, JobItemState to, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (seqs is null) throw new ArgumentNullException(nameof(seqs));
        if (seqs.Count == 0) return Task.CompletedTask;

        var now = FormatTime(DateTime.UtcNow);
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            foreach (var seq in seqs)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE job_items SET
    state = @state,
    error_code = NULL,
    error_detail = NULL,
    updated_at = @updatedAt
WHERE job_id = @jobId AND seq = @seq;";
                command.Parameters.AddWithValue("@state", (int)to);
                command.Parameters.AddWithValue("@updatedAt", now);
                command.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
                command.Parameters.AddWithValue("@seq", seq);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<int> BeginBatchAsync(Guid jobId, int fromSeq, int toSeq, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = FormatTime(DateTime.UtcNow);
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            int batchIndex;
            using (var next = connection.CreateCommand())
            {
                next.Transaction = transaction;
                next.CommandText = @"
SELECT COALESCE(MAX(batch_index), -1) + 1 FROM job_batches WHERE job_id = @jobId;";
                next.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
                batchIndex = Convert.ToInt32(next.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = @"
INSERT INTO job_batches(job_id, batch_index, from_seq, to_seq, status, started_at)
VALUES(@jobId, @batchIndex, @fromSeq, @toSeq, 'started', @startedAt);";
                insert.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
                insert.Parameters.AddWithValue("@batchIndex", batchIndex);
                insert.Parameters.AddWithValue("@fromSeq", fromSeq);
                insert.Parameters.AddWithValue("@toSeq", toSeq);
                insert.Parameters.AddWithValue("@startedAt", now);
                insert.ExecuteNonQuery();
            }

            AppendEventCore(
                connection,
                transaction,
                jobId,
                DateTime.UtcNow,
                JobEventTypes.BatchStarted,
                "{\"batch\":" + batchIndex.ToString(CultureInfo.InvariantCulture) +
                ",\"from\":" + fromSeq.ToString(CultureInfo.InvariantCulture) +
                ",\"to\":" + toSeq.ToString(CultureInfo.InvariantCulture) + "}");
            transaction.Commit();
            return Task.FromResult(batchIndex);
        }
    }

    public Task CompleteBatchAsync(
        Guid jobId,
        int batchIndex,
        int applied,
        int failed,
        bool saved,
        DateTime? savedAtUtc,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = FormatTime(DateTime.UtcNow);
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE job_batches SET
    status = 'finished',
    applied_count = @applied,
    failed_count = @failed,
    saved = @saved,
    saved_at = @savedAt,
    finished_at = @finishedAt
WHERE job_id = @jobId AND batch_index = @batchIndex;";
                command.Parameters.AddWithValue("@applied", applied);
                command.Parameters.AddWithValue("@failed", failed);
                command.Parameters.AddWithValue("@saved", saved ? 1 : 0);
                command.Parameters.AddWithValue("@savedAt", (object?)(savedAtUtc is null ? null : FormatTime(savedAtUtc.Value)) ?? DBNull.Value);
                command.Parameters.AddWithValue("@finishedAt", now);
                command.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
                command.Parameters.AddWithValue("@batchIndex", batchIndex);
                command.ExecuteNonQuery();
            }

            AppendEventCore(
                connection,
                transaction,
                jobId,
                DateTime.UtcNow,
                saved ? JobEventTypes.Saved : JobEventTypes.BatchCommitted,
                "{\"batch\":" + batchIndex.ToString(CultureInfo.InvariantCulture) +
                ",\"applied\":" + applied.ToString(CultureInfo.InvariantCulture) +
                ",\"failed\":" + failed.ToString(CultureInfo.InvariantCulture) +
                ",\"saved\":" + (saved ? "true" : "false") + "}");
            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task AppendEventAsync(Guid jobId, string type, string? payloadJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            AppendEventCore(connection, transaction, jobId, DateTime.UtcNow, type, payloadJson);
            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobEvent>> LoadEventsAsync(Guid jobId, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (limit <= 0) limit = 100;

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id, job_id, ts, type, payload_json
FROM job_events
WHERE job_id = @jobId
ORDER BY id
LIMIT @limit;";
            command.Parameters.AddWithValue("@jobId", jobId.ToString("D"));
            command.Parameters.AddWithValue("@limit", limit);

            var events = new List<JobEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new JobEvent
                {
                    Id = reader.GetInt64(0),
                    JobId = Guid.Parse(reader.GetString(1)),
                    At = ParseTime(reader.GetString(2)),
                    Type = reader.GetString(3),
                    PayloadJson = reader.IsDBNull(4) ? null : reader.GetString(4)
                });
            }

            return Task.FromResult<IReadOnlyList<JobEvent>>(events);
        }
    }

    private static string BuildStateFilter(IReadOnlyList<JobItemState> states)
    {
        if (states is null || states.Count == 0)
            return "(0, 1, 2, 3, 4, 5, 6, 7, 8, 9)";

        var builder = new StringBuilder("(");
        for (var i = 0; i < states.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(((int)states[i]).ToString(CultureInfo.InvariantCulture));
        }

        return builder.Append(')').ToString();
    }

    private static IReadOnlyList<JobItem> ReadItems(SqliteCommand command)
    {
        var items = new List<JobItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new JobItem
            {
                Seq = reader.GetInt32(0),
                UnitKey = reader.GetString(1),
                ArgsJson = reader.GetString(2),
                Label = reader.IsDBNull(3) ? null : reader.GetString(3),
                State = (JobItemState)reader.GetInt32(4),
                PreviewJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                ExpectedJson = reader.IsDBNull(6) ? null : reader.GetString(6),
                ActualJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                UndoJson = reader.IsDBNull(8) ? null : reader.GetString(8),
                ErrorCode = reader.IsDBNull(9) ? null : reader.GetString(9),
                ErrorDetail = reader.IsDBNull(10) ? null : reader.GetString(10),
                Attempts = reader.GetInt32(11),
                BatchIndex = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                UpdatedAt = ParseTimeOrNull(reader.IsDBNull(13) ? null : reader.GetString(13))
            });
        }

        return items;
    }
}