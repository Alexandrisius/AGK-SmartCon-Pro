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

    public Task RecordAsync(
        string sessionId,
        string provider,
        string model,
        AgentUsage usage,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (usage is null) throw new ArgumentNullException(nameof(usage));

        lock (_database.WriteLock)
        {
            using var connection = _database.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO token_usage(id, session_id, provider, model, prompt_tokens, completion_tokens,
                        reasoning_tokens, cost_usd, created_at)
VALUES(@id, @sessionId, @provider, @model, @promptTokens, @completionTokens,
       @reasoningTokens, @costUsd, @createdAt);";
            command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@sessionId", sessionId);
            command.Parameters.AddWithValue("@provider", provider);
            command.Parameters.AddWithValue("@model", model);
            command.Parameters.AddWithValue("@promptTokens", usage.PromptTokens);
            command.Parameters.AddWithValue("@completionTokens", usage.CompletionTokens);
            command.Parameters.AddWithValue("@reasoningTokens", usage.ReasoningTokens);
            command.Parameters.AddWithValue(
                "@costUsd",
                usage.CostUsd.HasValue
                    ? usage.CostUsd.Value.ToString(CultureInfo.InvariantCulture)
                    : (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "@createdAt",
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }
}
