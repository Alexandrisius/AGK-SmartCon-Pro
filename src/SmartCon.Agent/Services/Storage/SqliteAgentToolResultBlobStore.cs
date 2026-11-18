using System.Globalization;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

public sealed class SqliteAgentToolResultBlobStore : IAgentToolResultBlobStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly AgentDatabase _database;

    public SqliteAgentToolResultBlobStore(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task SaveAsync(
        Guid sessionId,
        string toolCallId,
        string resultJson,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(toolCallId))
            throw new ArgumentException("Tool call id is required.", nameof(toolCallId));
        if (resultJson is null)
            throw new ArgumentNullException(nameof(resultJson));

        var now = DateTime.UtcNow;
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var prune = connection.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText =
                    "DELETE FROM tool_result_blobs WHERE expires_at IS NOT NULL AND expires_at < @now;";
                prune.Parameters.AddWithValue("@now", Format(now));
                prune.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO tool_result_blobs(tool_call_id, session_id, result_json, created_at, expires_at)
VALUES(@handle, @sessionId, @resultJson, @createdAt, @expiresAt);";
                command.Parameters.AddWithValue("@handle", toolCallId);
                command.Parameters.AddWithValue("@sessionId", sessionId.ToString("D"));
                command.Parameters.AddWithValue("@resultJson", resultJson);
                command.Parameters.AddWithValue("@createdAt", Format(now));
                command.Parameters.AddWithValue("@expiresAt", Format(now + Retention));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<AgentToolResultBlob?> GetAsync(
        Guid sessionId,
        string toolCallId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(toolCallId))
            return Task.FromResult<AgentToolResultBlob?>(null);

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT result_json, created_at
FROM tool_result_blobs
WHERE tool_call_id = @handle
  AND session_id = @sessionId
  AND (expires_at IS NULL OR expires_at > @now);";
            command.Parameters.AddWithValue("@handle", toolCallId);
            command.Parameters.AddWithValue("@sessionId", sessionId.ToString("D"));
            command.Parameters.AddWithValue("@now", Format(DateTime.UtcNow));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<AgentToolResultBlob?>(null);

            return Task.FromResult<AgentToolResultBlob?>(new AgentToolResultBlob
            {
                ToolCallId = toolCallId,
                SessionId = sessionId,
                ResultJson = reader.GetString(0),
                CreatedAt = DateTime.Parse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
            });
        }
    }

    private static string Format(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
