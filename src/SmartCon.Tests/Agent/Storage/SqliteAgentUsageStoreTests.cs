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

    [Fact]
    public async Task GetSessionMetricsAsync_AggregatesUsageAndToolCalls()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var usageStore = new SqliteAgentUsageStore(fixture.Database);
        var toolStore = new SqliteAgentToolCallStore(fixture.Database);
        var sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var otherId = Guid.Parse("99999999-8888-7777-6666-555555555555");

        await usageStore.RecordAsync(
            new AgentUsageRecord
            {
                SessionId = sessionId.ToString("D"),
                Provider = "openrouter",
                Model = "model-x",
                Usage = new AgentUsage
                {
                    PromptTokens = 100,
                    CompletionTokens = 20,
                    ReasoningTokens = 5,
                    CachedTokens = 40,
                    CostUsd = 0.001m
                },
                Status = "ok",
                LatencyMs = 300
            },
            CancellationToken.None);
        await usageStore.RecordAsync(
            new AgentUsageRecord
            {
                SessionId = sessionId.ToString("D"),
                Provider = "openrouter",
                Model = "model-x",
                Usage = new AgentUsage
                {
                    PromptTokens = 150,
                    CompletionTokens = 30,
                    CostUsd = 0.002m
                },
                Status = "ok",
                LatencyMs = 200
            },
            CancellationToken.None);
        await usageStore.RecordAsync(
            new AgentUsageRecord
            {
                SessionId = otherId.ToString("D"),
                Provider = "openrouter",
                Model = "model-x",
                Usage = new AgentUsage { PromptTokens = 9_999 },
                Status = "ok",
                LatencyMs = 9_999
            },
            CancellationToken.None);

        await toolStore.RecordAsync(
            new AgentToolCallRecord
            {
                Id = "t1",
                SessionId = sessionId.ToString("D"),
                ToolName = "query_elements",
                Status = "ok",
                DurationMs = 120
            },
            CancellationToken.None);
        await toolStore.RecordAsync(
            new AgentToolCallRecord
            {
                Id = "t2",
                SessionId = sessionId.ToString("D"),
                ToolName = "start_job",
                Status = "error",
                DurationMs = 80
            },
            CancellationToken.None);
        await toolStore.RecordAsync(
            new AgentToolCallRecord
            {
                Id = "t3",
                SessionId = otherId.ToString("D"),
                ToolName = "list_views",
                Status = "ok",
                DurationMs = 9_999
            },
            CancellationToken.None);

        var metrics = await usageStore.GetSessionMetricsAsync(sessionId, CancellationToken.None);

        Assert.NotNull(metrics);
        Assert.Equal(2, metrics!.Turns);
        Assert.Equal(2, metrics.ToolCalls);
        Assert.Equal(1, metrics.FailedToolCalls);
        Assert.Equal(250, metrics.PromptTokens);
        Assert.Equal(50, metrics.CompletionTokens);
        Assert.Equal(5, metrics.ReasoningTokens);
        Assert.Equal(40, metrics.CachedTokens);
        Assert.Equal(0.003m, metrics.CostUsd);
        Assert.Equal(500, metrics.LlmLatencyMs);
        Assert.Equal(200, metrics.ToolDurationMs);
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
