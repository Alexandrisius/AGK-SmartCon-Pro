using System.Globalization;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

public sealed class SqliteAgentAuditLog : IAgentAuditLog
{
    private readonly AgentDatabase _database;

    public SqliteAgentAuditLog(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task WriteAsync(AgentAuditEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (entry is null) throw new ArgumentNullException(nameof(entry));

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO audit_log(ts, actor, document_path, tool, args_hash, result,
                      affected_count, duration_ms, error_code)
VALUES(@ts, @actor, @documentPath, @tool, @argsHash, @result,
       @affectedCount, @durationMs, @errorCode);";
            command.Parameters.AddWithValue("@ts", entry.Timestamp.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@actor", (object?)entry.Actor ?? DBNull.Value);
            command.Parameters.AddWithValue("@documentPath", (object?)entry.DocumentPath ?? DBNull.Value);
            command.Parameters.AddWithValue("@tool", entry.Tool);
            command.Parameters.AddWithValue("@argsHash", entry.ArgsHash);
            command.Parameters.AddWithValue("@result", entry.Result);
            command.Parameters.AddWithValue("@affectedCount", entry.AffectedCount);
            command.Parameters.AddWithValue("@durationMs", entry.DurationMs);
            command.Parameters.AddWithValue("@errorCode", (object?)entry.ErrorCode ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }
}