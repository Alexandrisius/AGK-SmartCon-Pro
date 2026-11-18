using System.Globalization;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

public sealed class SqliteAgentCompactionStore : IAgentCompactionStore
{
    private readonly AgentDatabase _database;

    public SqliteAgentCompactionStore(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task SaveAsync(AgentCompactionRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (record is null) throw new ArgumentNullException(nameof(record));

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT OR REPLACE INTO compactions(id, session_id, from_rowid, to_rowid, summary,
                                    tokens_before, tokens_after, source_prefix_hash,
                                    model, ack_message_id, created_at)
VALUES(@id, @sessionId, @fromRowId, @toRowId, @summary,
       @tokensBefore, @tokensAfter, @prefixHash,
       @model, @ackMessageId, @createdAt);";
            command.Parameters.AddWithValue("@id", record.Id);
            command.Parameters.AddWithValue("@sessionId", record.SessionId.ToString("D"));
            command.Parameters.AddWithValue("@fromRowId", record.FromRowId);
            command.Parameters.AddWithValue("@toRowId", record.ToRowId);
            command.Parameters.AddWithValue("@summary", record.Summary);
            command.Parameters.AddWithValue("@tokensBefore", record.TokensBefore);
            command.Parameters.AddWithValue("@tokensAfter", record.TokensAfter);
            command.Parameters.AddWithValue("@prefixHash", record.SourcePrefixHash);
            command.Parameters.AddWithValue("@model", (object?)record.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("@ackMessageId", (object?)record.AckMessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", Format(record.CreatedAt));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task<AgentCompactionRecord?> GetLatestAsync(Guid sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT id, from_rowid, to_rowid, summary, tokens_before, tokens_after,
       source_prefix_hash, model, ack_message_id, created_at
FROM compactions
WHERE session_id = @sessionId
ORDER BY created_at DESC
LIMIT 1;";
            command.Parameters.AddWithValue("@sessionId", sessionId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<AgentCompactionRecord?>(null);

            return Task.FromResult<AgentCompactionRecord?>(new AgentCompactionRecord
            {
                Id = reader.GetString(0),
                SessionId = sessionId,
                FromRowId = reader.GetInt32(1),
                ToRowId = reader.GetInt32(2),
                Summary = reader.GetString(3),
                TokensBefore = reader.GetInt32(4),
                TokensAfter = reader.GetInt32(5),
                SourcePrefixHash = reader.GetString(6),
                Model = reader.IsDBNull(7) ? null : reader.GetString(7),
                AckMessageId = reader.IsDBNull(8) ? null : reader.GetString(8),
                CreatedAt = DateTime.Parse(
                    reader.GetString(9),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
            });
        }
    }

    private static string Format(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
