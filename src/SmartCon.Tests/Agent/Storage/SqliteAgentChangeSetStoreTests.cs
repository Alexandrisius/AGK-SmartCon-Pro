using System.IO;
using Xunit;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Storage;

public sealed class SqliteAgentChangeSetStoreTests
{
    [Fact]
    public async Task BeginAddItemsMarkApplied_ReturnsDetailsAndLastApplied()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentChangeSetStore(fixture.Database);

        var id = await store.BeginAsync(Header("doc-1", "rename_views"), CancellationToken.None);
        await store.AddItemsAsync(id, new[] { Item(id, "view_name", "A", "B") }, CancellationToken.None);
        await store.MarkAppliedAsync(id, DateTime.UtcNow, CancellationToken.None);

        var details = await store.GetAsync(id, CancellationToken.None);
        Assert.NotNull(details);
        Assert.Single(details!.Items);
        Assert.Equal("view_name", details.Items[0].ParameterName);
        Assert.NotNull(details.Header.AppliedAt);

        var last = await store.GetLastAppliedAsync("doc-1", CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal(id, last!.Header.Id);
    }

    [Fact]
    public async Task MarkRolledBack_ClearsAppliedAndMarksItems()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentChangeSetStore(fixture.Database);

        var id = await store.BeginAsync(Header("doc-1", "rename_views"), CancellationToken.None);
        await store.AddItemsAsync(id, new[] { Item(id, "view_name", "A", "B") }, CancellationToken.None);
        await store.MarkAppliedAsync(id, DateTime.UtcNow, CancellationToken.None);

        await store.MarkRolledBackAsync(id, DateTime.UtcNow, CancellationToken.None);

        var details = await store.GetAsync(id, CancellationToken.None);
        Assert.NotNull(details);
        Assert.Null(details!.Header.AppliedAt);
        Assert.Equal(ChangeStatus.RolledBack, details.Items[0].Status);

        var last = await store.GetLastAppliedAsync("doc-1", CancellationToken.None);
        Assert.Null(last);
    }

    [Fact]
    public async Task GetLastApplied_IgnoresOtherDocuments()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentChangeSetStore(fixture.Database);

        var id = await store.BeginAsync(Header("doc-2", "rename_views"), CancellationToken.None);
        await store.AddItemsAsync(id, new[] { Item(id, "view_name", "A", "B") }, CancellationToken.None);
        await store.MarkAppliedAsync(id, DateTime.UtcNow, CancellationToken.None);

        var last = await store.GetLastAppliedAsync("doc-1", CancellationToken.None);
        Assert.Null(last);
    }

    private static ChangeSetHeader Header(string documentId, string operation) => new()
    {
        Operation = operation,
        DocumentId = documentId,
        DocumentPath = "C:/models/test.rvt",
        RevitVersion = "2025",
        Author = "test",
        Description = "unit test"
    };

    private static ChangeItem Item(Guid setId, string parameter, string oldValue, string newValue) => new()
    {
        ChangeSetId = setId,
        UniqueId = "uid-1",
        ElementId = 42,
        ParameterName = parameter,
        OldValue = oldValue,
        NewValue = newValue,
        Status = ChangeStatus.Applied
    };

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "smartcon-agent-changeset-tests",
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
