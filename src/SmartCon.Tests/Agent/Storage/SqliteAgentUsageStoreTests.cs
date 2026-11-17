using System.IO;
using Xunit;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Storage;

public sealed class SqliteAgentUsageStoreTests
{
    [Fact]
    public async Task RecordAsync_PersistsUsageRow()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentUsageStore(fixture.Database);

        await store.RecordAsync(
            "session-1",
            "openrouter",
            "model-x",
            new AgentUsage
            {
                PromptTokens = 120,
                CompletionTokens = 30,
                ReasoningTokens = 10,
                TotalTokens = 160,
                CostUsd = 0.0012m
            },
            CancellationToken.None);

        using var connection = fixture.Database.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT session_id, provider, model, prompt_tokens, completion_tokens, reasoning_tokens, cost_usd FROM token_usage;";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("session-1", reader.GetString(0));
        Assert.Equal("openrouter", reader.GetString(1));
        Assert.Equal("model-x", reader.GetString(2));
        Assert.Equal(120, reader.GetInt32(3));
        Assert.Equal(30, reader.GetInt32(4));
        Assert.Equal(10, reader.GetInt32(5));
        Assert.Equal("0.0012", reader.GetString(6));
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
