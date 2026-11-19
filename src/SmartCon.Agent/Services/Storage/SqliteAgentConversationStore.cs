using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

public sealed class SqliteAgentConversationStore : IAgentConversationStore
{
    private readonly AgentDatabase _database;

    public SqliteAgentConversationStore(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task<Guid> CreateConversationAsync(AgentConversationHeader header, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (header is null) throw new ArgumentNullException(nameof(header));

        var id = header.Id == Guid.Empty ? Guid.NewGuid() : header.Id;
        var now = DateTime.UtcNow;
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO sessions(id, document_id, title, document_path, revit_version,
                     created_at, updated_at, token_count)
VALUES(@id, @documentId, @title, @documentPath, @revitVersion, @createdAt, @updatedAt, @tokenCount);";
            command.Parameters.AddWithValue("@id", id.ToString("D"));
            command.Parameters.AddWithValue("@documentId", header.DocumentId);
            command.Parameters.AddWithValue("@title", header.Title);
            command.Parameters.AddWithValue("@documentPath", (object?)header.DocumentPath ?? DBNull.Value);
            command.Parameters.AddWithValue("@revitVersion", (object?)header.RevitVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", Format(now));
            command.Parameters.AddWithValue("@updatedAt", Format(now));
            command.Parameters.AddWithValue("@tokenCount", header.TokenCount);
            command.ExecuteNonQuery();
        }

        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<AgentConversationHeader>> ListConversationsAsync(
        string documentId,
        int limit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var safeLimit = Math.Max(1, Math.Min(limit, 100));
        var result = new List<AgentConversationHeader>();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id, document_id, title, document_path, revit_version, created_at, updated_at, token_count
FROM sessions
WHERE document_id = @documentId
ORDER BY updated_at DESC
LIMIT @limit;";
            command.Parameters.AddWithValue("@documentId", documentId);
            command.Parameters.AddWithValue("@limit", safeLimit);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    result.Add(ReadHeader(reader));
            }

            BackfillLegacyTitles(connection, result);
        }

        return Task.FromResult<IReadOnlyList<AgentConversationHeader>>(result);
    }

    public Task<AgentConversationHeader?> GetConversationAsync(Guid conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id, document_id, title, document_path, revit_version, created_at, updated_at, token_count
FROM sessions WHERE id = @id;";
            command.Parameters.AddWithValue("@id", conversationId.ToString("D"));
            using var reader = command.ExecuteReader();
            return Task.FromResult(reader.Read() ? ReadHeader(reader) : null);
        }
    }

    public Task AppendMessageAsync(Guid conversationId, AgentMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (message is null) throw new ArgumentNullException(nameof(message));

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO messages(id, session_id, role, content, reasoning_content,
                     tool_calls_json, tool_call_id, attachments_json, created_at, token_count)
VALUES(@id, @sessionId, @role, @content, @reasoning, @toolCalls, @toolCallId, @attachments, @createdAt, @tokenCount);";
                command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("@sessionId", conversationId.ToString("D"));
                command.Parameters.AddWithValue("@role", message.Role.ToString());
                command.Parameters.AddWithValue("@content", (object?)message.Content ?? DBNull.Value);
                command.Parameters.AddWithValue("@reasoning", (object?)message.ReasoningContent ?? DBNull.Value);
                command.Parameters.AddWithValue("@toolCalls", (object?)SerializeToolCalls(message.ToolCalls) ?? DBNull.Value);
                command.Parameters.AddWithValue("@toolCallId", (object?)message.ToolCallId ?? DBNull.Value);
                command.Parameters.AddWithValue("@attachments", (object?)SerializeAttachments(message.Attachments) ?? DBNull.Value);
                command.Parameters.AddWithValue("@createdAt", Format(message.CreatedAt));
                command.Parameters.AddWithValue("@tokenCount", message.TokenCount);
                command.ExecuteNonQuery();
            }

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"
UPDATE sessions SET updated_at = @updatedAt,
                    token_count = token_count + @tokenCount
WHERE id = @id;";
                update.Parameters.AddWithValue("@updatedAt", Format(DateTime.UtcNow));
                update.Parameters.AddWithValue("@tokenCount", message.TokenCount);
                update.Parameters.AddWithValue("@id", conversationId.ToString("D"));
                update.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(Guid conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<AgentMessage>();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT role, content, reasoning_content, tool_calls_json, tool_call_id, attachments_json,
       created_at, token_count
FROM messages
WHERE session_id = @sessionId
ORDER BY rowid;";
            command.Parameters.AddWithValue("@sessionId", conversationId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var message = ReadMessage(reader);
                result.Add(message.WithSessionIndex(result.Count + 1));
            }
        }

        return Task.FromResult<IReadOnlyList<AgentMessage>>(result);
    }

    public Task UpdateTitleAsync(Guid conversationId, string title, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET title = @title, updated_at = @updatedAt WHERE id = @id;";
            command.Parameters.AddWithValue("@title", title);
            command.Parameters.AddWithValue("@updatedAt", Format(DateTime.UtcNow));
            command.Parameters.AddWithValue("@id", conversationId.ToString("D"));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task DeleteConversationAsync(Guid conversationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            Execute(connection, transaction, "DELETE FROM messages WHERE session_id = @id;", conversationId);
            Execute(connection, transaction, "DELETE FROM tool_calls WHERE session_id = @id;", conversationId);
            Execute(connection, transaction, "DELETE FROM token_usage WHERE session_id = @id;", conversationId);
            Execute(connection, transaction, "DELETE FROM compactions WHERE session_id = @id;", conversationId);
            Execute(connection, transaction, "DELETE FROM tool_result_blobs WHERE session_id = @id;", conversationId);
            Execute(connection, transaction, "DELETE FROM working_states WHERE session_id = @id;", conversationId);
            Execute(connection, transaction, "DELETE FROM sessions WHERE id = @id;", conversationId);

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static AgentConversationHeader ReadHeader(SqliteDataReader reader)
    {
        return new AgentConversationHeader
        {
            Id = Guid.Parse(reader.GetString(0)),
            DocumentId = reader.GetString(1),
            Title = reader.GetString(2),
            DocumentPath = reader.IsDBNull(3) ? null : reader.GetString(3),
            RevitVersion = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = Parse(reader.GetString(5)),
            UpdatedAt = Parse(reader.GetString(6)),
            TokenCount = reader.GetInt32(7)
        };
    }

    /// <summary>
    /// Sessions created before the 200-char title policy still carry the old
    /// 60-char cut (61 with the ellipsis). Rebuild those titles from the first
    /// user message once, so existing sessions also use the panel width.
    /// </summary>
    private static void BackfillLegacyTitles(
        SqliteConnection connection,
        List<AgentConversationHeader> headers)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            if (header.Title.Length != 61
                || !header.Title.EndsWith("…", StringComparison.Ordinal))
            {
                continue;
            }

            using var read = connection.CreateCommand();
            read.CommandText = @"
SELECT content FROM messages
WHERE session_id = @id AND role = 'User' AND length(content) > 60
ORDER BY rowid LIMIT 1;";
            read.Parameters.AddWithValue("@id", header.Id.ToString("D"));
            if (read.ExecuteScalar() is not string content)
                continue;

            var title = AgentConversationTitle.FromFirstMessage(content);
            if (title.Length <= header.Title.Length)
                continue;

            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE sessions SET title = @title WHERE id = @id;";
            update.Parameters.AddWithValue("@title", title);
            update.Parameters.AddWithValue("@id", header.Id.ToString("D"));
            update.ExecuteNonQuery();

            headers[i] = new AgentConversationHeader
            {
                Id = header.Id,
                Title = title,
                DocumentId = header.DocumentId,
                DocumentPath = header.DocumentPath,
                RevitVersion = header.RevitVersion,
                CreatedAt = header.CreatedAt,
                UpdatedAt = header.UpdatedAt,
                TokenCount = header.TokenCount
            };
        }
    }

    private static AgentMessage ReadMessage(SqliteDataReader reader)
    {
        var role = reader.GetString(0);
        return new AgentMessage
        {
            Role = Enum.TryParse<AgentRole>(role, out var parsed) ? parsed : AgentRole.User,
            Content = reader.IsDBNull(1) ? null : reader.GetString(1),
            ReasoningContent = reader.IsDBNull(2) ? null : reader.GetString(2),
            ToolCalls = DeserializeToolCalls(reader.IsDBNull(3) ? null : reader.GetString(3)),
            ToolCallId = reader.IsDBNull(4) ? null : reader.GetString(4),
            Attachments = DeserializeAttachments(reader.IsDBNull(5) ? null : reader.GetString(5)),
            CreatedAt = Parse(reader.GetString(6)),
            TokenCount = reader.GetInt32(7)
        };
    }

    private static string? SerializeToolCalls(IReadOnlyList<AgentToolCall> calls) =>
        calls.Count == 0 ? null : JsonSerializer.Serialize(calls);

    private static IReadOnlyList<AgentToolCall> DeserializeToolCalls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<AgentToolCall>();
        return JsonSerializer.Deserialize<List<AgentToolCall>>(json!) ?? new List<AgentToolCall>();
    }

    private static string? SerializeAttachments(IReadOnlyList<AgentAttachment> attachments) =>
        attachments.Count == 0 ? null : JsonSerializer.Serialize(attachments);

    private static IReadOnlyList<AgentAttachment> DeserializeAttachments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<AgentAttachment>();
        return JsonSerializer.Deserialize<List<AgentAttachment>>(json!) ?? new List<AgentAttachment>();
    }

    private static string Format(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.UtcNow;
}