using System.IO;
using Xunit;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Storage;

public sealed class SqliteAgentUsageStoreTests
{
    [Fact]
    public async Task RecordAsync_PersistsUsageAndTurnMetadata()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentUsageStore(fixture.Database);

        await store.RecordAsync(
            new AgentUsageRecord
            {
                SessionId = "session-1",
                Provider = "openrouter",
                Model = "model-x",
                Usage = new AgentUsage
                {
                    PromptTokens = 120,
                    CompletionTokens = 30,
                    ReasoningTokens = 10,
                    CachedTokens = 40,
                    CacheWriteTokens = 8,
                    TotalTokens = 160,
                    CostUsd = 0.0012m
                },
                Status = "ok",
                LatencyMs = 1234,
                RequestId = "req-1",
                CompactionId = "cmp-1",
                EstimatedPromptTokens = 130
            },
            CancellationToken.None);

        using var connection = fixture.Database.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT session_id, provider, model, prompt_tokens, completion_tokens, reasoning_tokens,
       cost_usd, cached_tokens, cache_write_tokens, latency_ms, status, request_id,
       compaction_id, estimated_prompt_tokens
FROM token_usage;";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("session-1", reader.GetString(0));
        Assert.Equal("openrouter", reader.GetString(1));
        Assert.Equal("model-x", reader.GetString(2));
        Assert.Equal(120, reader.GetInt32(3));
        Assert.Equal(30, reader.GetInt32(4));
        Assert.Equal(10, reader.GetInt32(5));
        Assert.Equal("0.0012", reader.GetString(6));
        Assert.Equal(40, reader.GetInt32(7));
        Assert.Equal(8, reader.GetInt32(8));
        Assert.Equal(1234, reader.GetInt64(9));
        Assert.Equal("ok", reader.GetString(10));
        Assert.Equal("req-1", reader.GetString(11));
        Assert.Equal("cmp-1", reader.GetString(12));
        Assert.Equal(130, reader.GetInt32(13));
    }

    [Fact]
    public async Task RecordAsync_AllowsNullOptionalMetadata()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentUsageStore(fixture.Database);

        await store.RecordAsync(
            new AgentUsageRecord
            {
                SessionId = "session-1",
                Provider = "fake",
                Model = "fake-model",
                Status = "error"
            },
            CancellationToken.None);

        using var connection = fixture.Database.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT latency_ms, request_id, compaction_id, status, cached_tokens, cache_write_tokens
FROM token_usage;";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal("error", reader.GetString(3));
        Assert.Equal(0, reader.GetInt32(4));
        Assert.Equal(0, reader.GetInt32(5));
    }

    [Fact]
    public async Task GetSessionCostAsync_SumsEveryTurnOfThatSession()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentUsageStore(fixture.Database);
        var sessionId = Guid.NewGuid();
        var otherSessionId = Guid.NewGuid();

        foreach (var (session, cost) in new[]
                 {
                     (sessionId, 0.0012m),
                     (sessionId, 0.5m),
                     (otherSessionId, 9m)
                 })
        {
            await store.RecordAsync(
                new AgentUsageRecord
                {
                    SessionId = session.ToString("D"),
                    Provider = "openrouter",
                    Model = "model-x",
                    Usage = new AgentUsage { CostUsd = cost },
                    Status = "ok"
                },
                CancellationToken.None);
        }

        var total = await store.GetSessionCostAsync(sessionId, CancellationToken.None);

        Assert.Equal(0.5012m, Math.Round(total, 6));
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "smartcon-agent-usage-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Database = new AgentDatabase(Path.Combine(_directory, "agent.db"));
        }

        public AgentDatabase Database { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
