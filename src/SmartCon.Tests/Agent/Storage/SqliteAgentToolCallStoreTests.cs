using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Storage;

public sealed class SqliteAgentToolCallStoreTests
{
    [Fact]
    public async Task RecordAsync_PersistsRepairsJson()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentToolCallStore(fixture.Database);

        await store.RecordAsync(
            new AgentToolCallRecord
            {
                Id = "call-1",
                SessionId = "session-1",
                ToolName = "rename_views",
                ArgumentsJson = """{"viewId":5}""",
                ResultJson = """{"success":true}""",
                Status = "ok",
                DurationMs = 12,
                RepairsJson = """["renamed 'view_id' -> 'viewId'"]"""
            },
            CancellationToken.None);

        using var connection = fixture.Database.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT repairs_json FROM tool_calls WHERE id = 'call-1';";
        var repairsJson = (string?)command.ExecuteScalar();

        Assert.Equal("""["renamed 'view_id' -> 'viewId'"]""", repairsJson);
    }

    [Fact]
    public async Task RecordAsync_AllowsNullRepairsJson()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentToolCallStore(fixture.Database);

        await store.RecordAsync(
            new AgentToolCallRecord
            {
                Id = "call-2",
                SessionId = "session-1",
                ToolName = "list_views",
                Status = "ok"
            },
            CancellationToken.None);

        using var connection = fixture.Database.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT repairs_json FROM tool_calls WHERE id = 'call-2';";
        Assert.Equal(DBNull.Value, command.ExecuteScalar());
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "smartcon-agent-tool-call-tests",
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
