using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using Xunit;

namespace SmartCon.Tests.Agent.Storage;

public sealed class SqliteAgentToolResultBlobStoreTests
{
    [Fact]
    public async Task SaveLoad_RoundTripsTheFullPayload()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentToolResultBlobStore(fixture.Database);
        var conversationId = Guid.NewGuid();
        var payload = new string('x', 60_000);

        await store.SaveAsync(conversationId, "call_1", payload, CancellationToken.None);
        var loaded = await store.GetAsync(conversationId, "call_1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("call_1", loaded!.ToolCallId);
        Assert.Equal(conversationId, loaded.SessionId);
        Assert.Equal(payload, loaded.ResultJson);
    }

    [Fact]
    public async Task Get_FromAnotherConversation_ReturnsNull()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentToolResultBlobStore(fixture.Database);

        await store.SaveAsync(Guid.NewGuid(), "call_1", "{}", CancellationToken.None);

        Assert.Null(await store.GetAsync(
            Guid.NewGuid(), "call_1", CancellationToken.None));
    }

    [Fact]
    public async Task Save_SameHandleTwice_Overwrites()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentToolResultBlobStore(fixture.Database);
        var conversationId = Guid.NewGuid();

        await store.SaveAsync(conversationId, "call_1", "first", CancellationToken.None);
        await store.SaveAsync(conversationId, "call_1", "second", CancellationToken.None);

        var loaded = await store.GetAsync(conversationId, "call_1", CancellationToken.None);
        Assert.Equal("second", loaded!.ResultJson);
    }

    [Fact]
    public async Task Get_UnknownHandle_ReturnsNull()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentToolResultBlobStore(fixture.Database);

        Assert.Null(await store.GetAsync(
            Guid.NewGuid(), "missing", CancellationToken.None));
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "smartcon-agent-blob-tests",
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
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
