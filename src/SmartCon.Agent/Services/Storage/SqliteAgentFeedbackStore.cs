using System.Globalization;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

public sealed class SqliteAgentFeedbackStore : IAgentFeedbackStore
{
    private readonly AgentDatabase _database;

    public SqliteAgentFeedbackStore(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task RecordAsync(AgentFeedbackEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (entry is null) throw new ArgumentNullException(nameof(entry));

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO agent_feedback(id, session_id, document_id, model, provider, tool_name, area,
                           rating, issue, suggestion, context_json, created_at)
VALUES(@id, @sessionId, @documentId, @model, @provider, @toolName, @area,
       @rating, @issue, @suggestion, @contextJson, @createdAt);";
            command.Parameters.AddWithValue("@id", entry.Id.ToString("D"));
            command.Parameters.AddWithValue("@sessionId", (object?)entry.SessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@documentId", (object?)entry.DocumentId ?? DBNull.Value);
            command.Parameters.AddWithValue("@model", (object?)entry.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("@provider", (object?)entry.Provider ?? DBNull.Value);
            command.Parameters.AddWithValue("@toolName", (object?)entry.ToolName ?? DBNull.Value);
            command.Parameters.AddWithValue("@area", entry.Area);
            command.Parameters.AddWithValue("@rating", entry.Rating);
            command.Parameters.AddWithValue("@issue", entry.Issue);
            command.Parameters.AddWithValue("@suggestion", (object?)entry.Suggestion ?? DBNull.Value);
            command.Parameters.AddWithValue("@contextJson", (object?)entry.ContextJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", Format(entry.CreatedAt));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentFeedbackEntry>> ListAsync(
        string? documentId,
        int limit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var safeLimit = Math.Max(1, Math.Min(limit, 500));
        var result = new List<AgentFeedbackEntry>();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = string.IsNullOrWhiteSpace(documentId)
                ? @"SELECT id, session_id, document_id, model, provider, tool_name, area, rating,
                           issue, suggestion, context_json, created_at
                    FROM agent_feedback
                    ORDER BY created_at DESC
                    LIMIT @limit;"
                : @"SELECT id, session_id, document_id, model, provider, tool_name, area, rating,
                           issue, suggestion, context_json, created_at
                    FROM agent_feedback
                    WHERE document_id = @documentId
                    ORDER BY created_at DESC
                    LIMIT @limit;";

            if (!string.IsNullOrWhiteSpace(documentId))
                command.Parameters.AddWithValue("@documentId", documentId);
            command.Parameters.AddWithValue("@limit", safeLimit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(Read(reader));
        }

        return Task.FromResult<IReadOnlyList<AgentFeedbackEntry>>(result);
    }

    private static AgentFeedbackEntry Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        SessionId = reader.IsDBNull(1) ? null : reader.GetString(1),
        DocumentId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Model = reader.IsDBNull(3) ? null : reader.GetString(3),
        Provider = reader.IsDBNull(4) ? null : reader.GetString(4),
        ToolName = reader.IsDBNull(5) ? null : reader.GetString(5),
        Area = reader.GetString(6),
        Rating = reader.GetInt32(7),
        Issue = reader.GetString(8),
        Suggestion = reader.IsDBNull(9) ? null : reader.GetString(9),
        ContextJson = reader.IsDBNull(10) ? null : reader.GetString(10),
        CreatedAt = Parse(reader.GetString(11))
    };

    private static string Format(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.UtcNow;
}
