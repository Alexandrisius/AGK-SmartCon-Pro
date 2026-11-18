using System.Globalization;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

public sealed class SqliteAgentToolCallStore : IAgentToolCallStore
{
    private readonly AgentDatabase _database;

    public SqliteAgentToolCallStore(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task RecordAsync(AgentToolCallRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (record is null) throw new ArgumentNullException(nameof(record));

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR REPLACE INTO tool_calls(id, session_id, tool_name, arguments_json, result_json,
                                   status, error, duration_ms, created_at, repairs_json)
VALUES(@id, @sessionId, @toolName, @argumentsJson, @resultJson,
       @status, @error, @durationMs, @createdAt, @repairsJson);";
            command.Parameters.AddWithValue("@id", record.Id);
            command.Parameters.AddWithValue("@sessionId", record.SessionId);
            command.Parameters.AddWithValue("@toolName", record.ToolName);
            command.Parameters.AddWithValue("@argumentsJson", (object?)record.ArgumentsJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@resultJson", (object?)record.ResultJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", record.Status);
            command.Parameters.AddWithValue("@error", (object?)record.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("@durationMs", record.DurationMs);
            command.Parameters.AddWithValue("@createdAt", Format(record.CreatedAt));
            command.Parameters.AddWithValue("@repairsJson", (object?)record.RepairsJson ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    private static string Format(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
