using System.IO;
using Xunit;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Storage;

public sealed class SqliteAgentConversationStoreTests
{
    [Fact]
    public void Initialize_CreatesSchemaInDeleteJournalMode()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();

        using var connection = fixture.Database.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)command.ExecuteScalar();

        Assert.Equal("delete", mode, ignoreCase: true);
    }

    [Fact]
    public async Task CreateAndListConversations_FiltersByDocument()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentConversationStore(fixture.Database);

        var first = await store.CreateConversationAsync(Header("doc-a", "Первый"), CancellationToken.None);
        var second = await store.CreateConversationAsync(Header("doc-a", "Второй"), CancellationToken.None);
        _ = await store.CreateConversationAsync(Header("doc-b", "Чужой"), CancellationToken.None);

        var list = await store.ListConversationsAsync("doc-a", 10, CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, item => item.Id == first && item.Title == "Первый");
        Assert.Contains(list, item => item.Id == second && item.Title == "Второй");
    }

    [Fact]
    public async Task AppendAndGetMessages_PreservesRoleAndToolCalls()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentConversationStore(fixture.Database);
        var id = await store.CreateConversationAsync(Header("doc", "Сессия"), CancellationToken.None);

        await store.AppendMessageAsync(id, AgentMessage.User("привет"), CancellationToken.None);
        await store.AppendMessageAsync(
            id,
            AgentMessage.Assistant(
                null,
                new[]
                {
                    new AgentToolCall
                    {
                        Id = "call_1",
                        Name = "list_views",
                        ArgumentsJson = "{\"limit\":10}"
                    }
                },
                "reasoning"),
            CancellationToken.None);
        await store.AppendMessageAsync(
            id,
            AgentMessage.ToolResult("call_1", "{\"success\":true}"),
            CancellationToken.None);

        var messages = await store.GetMessagesAsync(id, CancellationToken.None);

        Assert.Equal(3, messages.Count);
        Assert.Equal(AgentRole.User, messages[0].Role);
        Assert.Equal("привет", messages[0].Content);

        Assert.Equal(AgentRole.Assistant, messages[1].Role);
        Assert.Null(messages[1].Content);
        Assert.Equal("reasoning", messages[1].ReasoningContent);
        Assert.Single(messages[1].ToolCalls);
        Assert.Equal("list_views", messages[1].ToolCalls[0].Name);
        Assert.Equal("{\"limit\":10}", messages[1].ToolCalls[0].ArgumentsJson);

        Assert.Equal(AgentRole.Tool, messages[2].Role);
        Assert.Equal("call_1", messages[2].ToolCallId);
    }

    [Fact]
    public async Task UpdateTitleAndDelete_RemovesConversationAndMessages()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentConversationStore(fixture.Database);
        var id = await store.CreateConversationAsync(Header("doc", "Новый чат"), CancellationToken.None);
        await store.AppendMessageAsync(id, AgentMessage.User("текст"), CancellationToken.None);

        await store.UpdateTitleAsync(id, "Переименовано", CancellationToken.None);
        var updated = await store.GetConversationAsync(id, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Переименовано", updated!.Title);

        await store.DeleteConversationAsync(id, CancellationToken.None);

        Assert.Null(await store.GetConversationAsync(id, CancellationToken.None));
        Assert.Empty(await store.GetMessagesAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task ChangeSetStore_RoundTripsHeaderAndItems()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentChangeSetStore(fixture.Database);
        var header = new ChangeSetHeader
        {
            Operation = "rename_views",
            DocumentId = "doc",
            DocumentPath = "C:\\test.rvt",
            RevitVersion = "2025",
            Author = "tester"
        };

        var id = await store.BeginAsync(header, CancellationToken.None);
        await store.AddItemsAsync(
            id,
            new[]
            {
                new ChangeItem
                {
                    ChangeSetId = id,
                    UniqueId = "abc",
                    ElementId = 42,
                    ParameterName = "Name",
                    OldValue = "Было",
                    NewValue = "Стало",
                    Status = ChangeStatus.Applied
                }
            },
            CancellationToken.None);
        await store.MarkAppliedAsync(id, DateTime.UtcNow, CancellationToken.None);

        var details = await store.GetAsync(id, CancellationToken.None);
        var lastApplied = await store.GetLastAppliedAsync("doc", CancellationToken.None);

        Assert.NotNull(details);
        Assert.Single(details!.Items);
        Assert.Equal("Было", details.Items[0].OldValue);
        Assert.NotNull(details.Header.AppliedAt);
        Assert.NotNull(lastApplied);
        Assert.Equal(id, lastApplied!.Header.Id);
    }

    [Fact]
    public async Task AuditLog_WriteAsync_PersistsEntry()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var audit = new SqliteAgentAuditLog(fixture.Database);

        await audit.WriteAsync(new AgentAuditEntry
        {
            Actor = "tester",
            DocumentPath = "C:\\test.rvt",
            Tool = "list_views",
            ArgsHash = "hash",
            Result = "ok",
            AffectedCount = 3,
            DurationMs = 12
        }, CancellationToken.None);

        using var connection = fixture.Database.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MAX(tool) FROM audit_log;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("list_views", reader.GetString(1));
    }

    private static AgentConversationHeader Header(string documentId, string title) => new()
    {
        Title = title,
        DocumentId = documentId,
        DocumentPath = "C:\\" + documentId + ".rvt",
        RevitVersion = "2025"
    };

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "smartcon-agent-tests",
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