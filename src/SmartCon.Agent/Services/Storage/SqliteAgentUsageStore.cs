using System.Globalization;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Storage;

public sealed class SqliteAgentUsageStore : IAgentUsageStore
{
    private readonly AgentDatabase _database;

    public SqliteAgentUsageStore(AgentDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public Task RecordAsync(AgentUsageRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (record is null) throw new ArgumentNullException(nameof(record));

        var usage = record.Usage ?? AgentUsage.Empty;

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO token_usage(id, session_id, provider, model, prompt_tokens, completion_tokens,
                        reasoning_tokens, cached_tokens, cache_write_tokens, cost_usd,
                        latency_ms, status, request_id, compaction_id,
                        estimated_prompt_tokens, context_tokens, context_limit, created_at)
VALUES(@id, @sessionId, @provider, @model, @promptTokens, @completionTokens,
       @reasoningTokens, @cachedTokens, @cacheWriteTokens, @costUsd,
       @latencyMs, @status, @requestId, @compactionId,
       @estimatedPromptTokens, @contextTokens, @contextLimit, @createdAt);";
            command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@sessionId", record.SessionId);
            command.Parameters.AddWithValue("@provider", record.Provider);
            command.Parameters.AddWithValue("@model", record.Model);
            command.Parameters.AddWithValue("@promptTokens", usage.PromptTokens);
            command.Parameters.AddWithValue("@completionTokens", usage.CompletionTokens);
            command.Parameters.AddWithValue("@reasoningTokens", usage.ReasoningTokens);
            command.Parameters.AddWithValue("@cachedTokens", usage.CachedTokens);
            command.Parameters.AddWithValue("@cacheWriteTokens", usage.CacheWriteTokens);
            command.Parameters.AddWithValue(
                "@costUsd",
                usage.CostUsd.HasValue
                    ? usage.CostUsd.Value.ToString(CultureInfo.InvariantCulture)
                    : (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "@latencyMs",
                record.LatencyMs.HasValue ? record.LatencyMs.Value : (object)DBNull.Value);
            command.Parameters.AddWithValue("@status", record.Status);
            command.Parameters.AddWithValue(
                "@requestId",
                string.IsNullOrEmpty(record.RequestId) ? (object)DBNull.Value : record.RequestId!);
            command.Parameters.AddWithValue(
                "@compactionId",
                string.IsNullOrEmpty(record.CompactionId) ? (object)DBNull.Value : record.CompactionId!);
            command.Parameters.AddWithValue("@estimatedPromptTokens", record.EstimatedPromptTokens);
            command.Parameters.AddWithValue("@contextTokens", record.ContextTokens);
            command.Parameters.AddWithValue("@contextLimit", record.ContextLimit);
            command.Parameters.AddWithValue(
                "@createdAt",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task<decimal> GetSessionCostAsync(Guid sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COALESCE(SUM(CAST(cost_usd AS REAL)), 0)
FROM token_usage
WHERE session_id = @sessionId;";
            command.Parameters.AddWithValue("@sessionId", sessionId.ToString("D"));

            var value = command.ExecuteScalar();
            if (value is null or DBNull)
                return Task.FromResult(0m);

            return Task.FromResult(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
        }
    }

    public Task<AgentContextSnapshot?> GetLastContextAsync(Guid sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT context_tokens, context_limit
FROM token_usage
WHERE session_id = @sessionId AND context_tokens > 0
ORDER BY created_at DESC
LIMIT 1;";
            command.Parameters.AddWithValue("@sessionId", sessionId.ToString("D"));

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<AgentContextSnapshot?>(null);

            return Task.FromResult<AgentContextSnapshot?>(new AgentContextSnapshot
            {
                Tokens = reader.GetInt32(0),
                Limit = reader.GetInt32(1)
            });
        }
    }
}
