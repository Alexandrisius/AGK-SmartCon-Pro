using System.IO;
using Xunit;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Tests.Agent.Storage;

public sealed class SqliteAgentWorkingStateStoreTests
{
    [Fact]
    public async Task SaveLoad_RoundTripsAllFields()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentWorkingStateStore(fixture.Database);
        var conversationId = Guid.NewGuid();
        var state = new WorkingState
        {
            Goal = "rename views",
            Constraints = { "не трогать листы" },
            PlanId = "digest-1",
            CompletedSteps = { "list_views: 3 changed" },
            PendingSteps = { "rename_views: failed" },
            ActiveViewId = 42,
            ActiveViewName = "Level 1",
            SelectedUniqueIds = { "uid-1" },
            TouchedElementIds = { "101", "102" },
            ChangeSetIds = { "cs-1" },
            OpenQuestions = { "user rejected rename_views" },
            DocumentId = "doc-1",
            DocumentPath = @"C:\models\test.rvt",
            RevitVersion = "2025"
        };

        await store.SaveAsync(conversationId, state, CancellationToken.None);
        var loaded = await store.LoadAsync(conversationId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(state.Goal, loaded!.Goal);
        Assert.Equal(state.Constraints, loaded.Constraints);
        Assert.Equal(state.PlanId, loaded.PlanId);
        Assert.Equal(state.CompletedSteps, loaded.CompletedSteps);
        Assert.Equal(state.PendingSteps, loaded.PendingSteps);
        Assert.Equal(state.ActiveViewId, loaded.ActiveViewId);
        Assert.Equal(state.ActiveViewName, loaded.ActiveViewName);
        Assert.Equal(state.SelectedUniqueIds, loaded.SelectedUniqueIds);
        Assert.Equal(state.TouchedElementIds, loaded.TouchedElementIds);
        Assert.Equal(state.ChangeSetIds, loaded.ChangeSetIds);
        Assert.Equal(state.OpenQuestions, loaded.OpenQuestions);
        Assert.Equal(state.DocumentId, loaded.DocumentId);
        Assert.Equal(state.DocumentPath, loaded.DocumentPath);
        Assert.Equal(state.RevitVersion, loaded.RevitVersion);
    }

    [Fact]
    public async Task Save_Twice_OverwritesTheSameRow()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentWorkingStateStore(fixture.Database);
        var conversationId = Guid.NewGuid();

        await store.SaveAsync(
            conversationId,
            new WorkingState { Goal = "first" },
            CancellationToken.None);
        await store.SaveAsync(
            conversationId,
            new WorkingState { Goal = "second" },
            CancellationToken.None);

        var loaded = await store.LoadAsync(conversationId, CancellationToken.None);
        Assert.Equal("second", loaded!.Goal);

        using var connection = fixture.Database.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM working_states WHERE session_id = @id;";
        command.Parameters.AddWithValue("@id", conversationId.ToString("D"));
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task Load_CorruptedJson_ReturnsNull()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentWorkingStateStore(fixture.Database);
        var conversationId = Guid.NewGuid();
        InsertRaw(fixture.Database, conversationId, "{not valid json");

        var loaded = await store.LoadAsync(conversationId, CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Load_EmptyStateJson_ReturnsNull()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentWorkingStateStore(fixture.Database);
        var conversationId = Guid.NewGuid();
        InsertRaw(fixture.Database, conversationId, string.Empty);

        var loaded = await store.LoadAsync(conversationId, CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Load_UnknownConversation_ReturnsNull()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentWorkingStateStore(fixture.Database);

        var loaded = await store.LoadAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Save_CancelledToken_Throws()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentWorkingStateStore(fixture.Database);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(Guid.NewGuid(), new WorkingState(), cts.Token));
    }

    [Fact]
    public async Task Load_JsonWithNullLists_ReturnsUsableNormalizedState()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentWorkingStateStore(fixture.Database);
        var conversationId = Guid.NewGuid();
        InsertRaw(
            fixture.Database,
            conversationId,
            """{"goal":"rename views","constraints":null,"completedSteps":null,"pendingSteps":null,"selectedUniqueIds":null,"touchedElementIds":null,"changeSetIds":null,"openQuestions":null}""");

        var loaded = await store.LoadAsync(conversationId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Constraints);
        Assert.Empty(loaded.CompletedSteps);
        Assert.Empty(loaded.PendingSteps);
        Assert.Empty(loaded.SelectedUniqueIds);
        Assert.Empty(loaded.TouchedElementIds);
        Assert.Empty(loaded.ChangeSetIds);
        Assert.Empty(loaded.OpenQuestions);

        AgentWorkingStateUpdater.UpdateFromUserText(loaded, "только планы");
        var text = AgentTurnContextBuilder.Build(
            loaded,
            new AgentProjectContext
            {
                DocumentId = "doc-1",
                Title = "Test",
                RevitVersion = "2025"
            });
        Assert.Contains("goal: rename views", text);
        Assert.Contains("- только планы", text);
    }

    private static void InsertRaw(AgentDatabase database, Guid conversationId, string json)
    {
        using var connection = database.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO working_states(session_id, state_json, updated_at)
VALUES(@sessionId, @stateJson, @updatedAt);";
        command.Parameters.AddWithValue("@sessionId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("@stateJson", json);
        command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "smartcon-agent-working-state-tests",
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
