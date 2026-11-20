using System.Globalization;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

public sealed class SqliteAgentChangeSetStore : IAgentChangeSetStore
{
    private readonly AgentDatabase _database;

    public SqliteAgentChangeSetStore(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task<Guid> BeginAsync(ChangeSetHeader header, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (header is null) throw new ArgumentNullException(nameof(header));

        var id = header.Id == Guid.Empty ? Guid.NewGuid() : header.Id;
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO change_sets(id, session_id, tool_call_id, operation, document_id, document_path,
                        revit_version, author, description, created_at, applied_at)
VALUES(@id, @sessionId, @toolCallId, @operation, @documentId, @documentPath,
       @revitVersion, @author, @description, @createdAt, NULL);";
            command.Parameters.AddWithValue("@id", id.ToString("D"));
            command.Parameters.AddWithValue("@sessionId", (object?)header.ConversationId?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("@toolCallId", (object?)header.ToolCallId ?? DBNull.Value);
            command.Parameters.AddWithValue("@operation", header.Operation);
            command.Parameters.AddWithValue("@documentId", header.DocumentId);
            command.Parameters.AddWithValue("@documentPath", (object?)header.DocumentPath ?? DBNull.Value);
            command.Parameters.AddWithValue("@revitVersion", (object?)header.RevitVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("@author", (object?)header.Author ?? DBNull.Value);
            command.Parameters.AddWithValue("@description", (object?)header.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", Format(header.CreatedAt));
            command.ExecuteNonQuery();
        }

        return Task.FromResult(id);
    }

    public Task AddItemsAsync(Guid changeSetId, IReadOnlyList<ChangeItem> items, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (items.Count == 0)
            return Task.CompletedTask;

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            foreach (var item in items)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO change_items(id, change_set_id, unique_id, element_id, parameter_name,
                         old_value, new_value, status, error)
VALUES(@id, @changeSetId, @uniqueId, @elementId, @parameterName,
       @oldValue, @newValue, @status, @error);";
                command.Parameters.AddWithValue("@id", item.Id.ToString("D"));
                command.Parameters.AddWithValue("@changeSetId", changeSetId.ToString("D"));
                command.Parameters.AddWithValue("@uniqueId", (object?)item.UniqueId ?? DBNull.Value);
                command.Parameters.AddWithValue("@elementId", (object?)item.ElementId ?? DBNull.Value);
                command.Parameters.AddWithValue("@parameterName", (object?)item.ParameterName ?? DBNull.Value);
                command.Parameters.AddWithValue("@oldValue", (object?)item.OldValue ?? DBNull.Value);
                command.Parameters.AddWithValue("@newValue", (object?)item.NewValue ?? DBNull.Value);
                command.Parameters.AddWithValue("@status", item.Status.ToString());
                command.Parameters.AddWithValue("@error", (object?)item.Error ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task MarkAppliedAsync(Guid changeSetId, DateTime appliedAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE change_sets SET applied_at = @appliedAt WHERE id = @id;";
            command.Parameters.AddWithValue("@appliedAt", Format(appliedAt));
            command.Parameters.AddWithValue("@id", changeSetId.ToString("D"));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChangeSetHeader>> ListAsync(
        string documentId,
        int limit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var safeLimit = Math.Max(1, Math.Min(limit, 100));
        var result = new List<ChangeSetHeader>();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id, session_id, tool_call_id, operation, document_id, document_path, revit_version,
       author, description, created_at, applied_at
FROM change_sets
WHERE document_id = @documentId
ORDER BY created_at DESC
LIMIT @limit;";
            command.Parameters.AddWithValue("@documentId", documentId);
            command.Parameters.AddWithValue("@limit", safeLimit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(ReadHeader(reader));
        }

        return Task.FromResult<IReadOnlyList<ChangeSetHeader>>(result);
    }

    public Task<ChangeSetDetails?> GetAsync(Guid changeSetId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();

            ChangeSetHeader? header = null;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT id, session_id, tool_call_id, operation, document_id, document_path, revit_version,
       author, description, created_at, applied_at
FROM change_sets WHERE id = @id;";
                command.Parameters.AddWithValue("@id", changeSetId.ToString("D"));
                using var reader = command.ExecuteReader();
                if (reader.Read())
                    header = ReadHeader(reader);
            }

            if (header is null)
                return Task.FromResult<ChangeSetDetails?>(null);

            var items = new List<ChangeItem>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT id, change_set_id, unique_id, element_id, parameter_name, old_value, new_value, status, error
FROM change_items WHERE change_set_id = @id ORDER BY rowid;";
                command.Parameters.AddWithValue("@id", changeSetId.ToString("D"));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    items.Add(ReadItem(reader));
            }

            return Task.FromResult<ChangeSetDetails?>(new ChangeSetDetails
            {
                Header = header,
                Items = items
            });
        }
    }

    public Task<ChangeSetDetails?> GetLastAppliedAsync(string documentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Guid? id = null;

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id FROM change_sets
WHERE document_id = @documentId AND applied_at IS NOT NULL
ORDER BY applied_at DESC, rowid DESC
LIMIT 1;";
            command.Parameters.AddWithValue("@documentId", documentId);
            var scalar = command.ExecuteScalar();
            if (scalar is string value && Guid.TryParse(value, out var parsed))
                id = parsed;
        }

        return id.HasValue ? GetAsync(id.Value, ct) : Task.FromResult<ChangeSetDetails?>(null);
    }

    public Task MarkRolledBackAsync(Guid changeSetId, DateTime rolledBackAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE change_sets SET applied_at = NULL WHERE id = @id;";
                command.Parameters.AddWithValue("@id", changeSetId.ToString("D"));
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE change_items SET status = @status WHERE change_set_id = @id;";
                command.Parameters.AddWithValue("@status", ChangeStatus.RolledBack.ToString());
                command.Parameters.AddWithValue("@id", changeSetId.ToString("D"));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        SmartConLogger.Info(
            "Agent change set rolled back: " + changeSetId.ToString("N") +
            " at " + rolledBackAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    public Task MarkDiscardedAsync(Guid changeSetId, DateTime discardedAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE change_sets SET applied_at = NULL WHERE id = @id;";
                command.Parameters.AddWithValue("@id", changeSetId.ToString("D"));
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE change_items SET status = @status, error = @error " +
                    "WHERE change_set_id = @id;";
                command.Parameters.AddWithValue("@status", ChangeStatus.Discarded.ToString());
                command.Parameters.AddWithValue("@error", "discarded by user");
                command.Parameters.AddWithValue("@id", changeSetId.ToString("D"));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        SmartConLogger.Info(
            "Agent change set discarded (model untouched): " + changeSetId.ToString("N") +
            " at " + discardedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    private static ChangeSetHeader ReadHeader(SqliteDataReader reader)
    {
        return new ChangeSetHeader
        {
            Id = Guid.Parse(reader.GetString(0)),
            ConversationId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            ToolCallId = reader.IsDBNull(2) ? null : reader.GetString(2),
            Operation = reader.GetString(3),
            DocumentId = reader.GetString(4),
            DocumentPath = reader.IsDBNull(5) ? null : reader.GetString(5),
            RevitVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
            Author = reader.IsDBNull(7) ? null : reader.GetString(7),
            Description = reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedAt = Parse(reader.GetString(9)),
            AppliedAt = reader.IsDBNull(10) ? null : Parse(reader.GetString(10))
        };
    }

    private static ChangeItem ReadItem(SqliteDataReader reader)
    {
        return new ChangeItem
        {
            Id = Guid.Parse(reader.GetString(0)),
            ChangeSetId = Guid.Parse(reader.GetString(1)),
            UniqueId = reader.IsDBNull(2) ? null : reader.GetString(2),
            ElementId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            ParameterName = reader.IsDBNull(4) ? null : reader.GetString(4),
            OldValue = reader.IsDBNull(5) ? null : reader.GetString(5),
            NewValue = reader.IsDBNull(6) ? null : reader.GetString(6),
            Status = Enum.TryParse<ChangeStatus>(reader.GetString(7), out var status)
                ? status
                : ChangeStatus.Pending,
            Error = reader.IsDBNull(8) ? null : reader.GetString(8)
        };
    }

    private static string Format(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.UtcNow;
}