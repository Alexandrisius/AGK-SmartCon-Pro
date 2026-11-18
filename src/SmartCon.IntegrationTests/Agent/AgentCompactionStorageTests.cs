using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Storage;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using TUnit.Core;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// H1-19: store-and-reference, checkpoint persistence and projection against a
/// real SQLite file (all three platforms, no Revit document needed).
/// </summary>
public sealed class AgentCompactionStorageTests
{
    static AgentCompactionStorageTests()
    {
        SQLitePCL.Batteries.Init();
    }

    [Test]
    public async Task BlobStore_SurvivesRestart_AndIsScopedToTheConversation()
    {
        var conversationId = Guid.NewGuid();
        var payload = "{\"success\":true,\"data\":{\"items\":\"" + new string('x', 50_000) + "\"}}";

        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();

        var first = new SqliteAgentToolResultBlobStore(fixture.Database);
        await first.SaveAsync(conversationId, "call_1", payload, CancellationToken.None);

        var restarted = new SqliteAgentToolResultBlobStore(fixture.Database);
        var loaded = await restarted.GetAsync(conversationId, "call_1", CancellationToken.None);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.ResultJson).IsEqualTo(payload);
        await Assert.That(await restarted.GetAsync(
            Guid.NewGuid(), "call_1", CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ReadToolResult_PagesThroughTheStoredBlob()
    {
        var conversationId = Guid.NewGuid();
        var views = Enumerable.Range(1, 300)
            .Select(index => new { id = index, name = "План " + index })
            .ToArray();
        var payload = JsonSerializer.Serialize(new { success = true, data = new { views } });

        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var blobs = new SqliteAgentToolResultBlobStore(fixture.Database);
        await blobs.SaveAsync(conversationId, "call_views", payload, CancellationToken.None);

        var tool = new ReadToolResultTool(blobs);
        var context = new AgentToolContext(new FakeAgentEvent(), "doc", conversationId);
        using var args = JsonDocument.Parse(
            "{\"handle\":\"call_views\",\"offset\":0,\"limit\":100}");

        var result = await tool.ExecuteAsync(
            context, args.RootElement, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("nextOffset").GetInt32()).IsEqualTo(100);
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(payload.Length);
    }

    [Test]
    public async Task CompactionStore_PersistsAndReturnsTheLatestCheckpoint()
    {
        var conversationId = Guid.NewGuid();
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentCompactionStore(fixture.Database);

        await store.SaveAsync(
            new AgentCompactionRecord
            {
                Id = "c1",
                SessionId = conversationId,
                ToRowId = 10,
                Summary = "first",
                TokensBefore = 1000,
                TokensAfter = 200,
                SourcePrefixHash = "hash1",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1)
            },
            CancellationToken.None);
        await store.SaveAsync(
            new AgentCompactionRecord
            {
                Id = "c2",
                SessionId = conversationId,
                ToRowId = 20,
                Summary = "second",
                TokensBefore = 1200,
                TokensAfter = 220,
                SourcePrefixHash = "hash2",
                Model = "summary-model",
                CreatedAt = DateTime.UtcNow
            },
            CancellationToken.None);

        var latest = await store.GetLatestAsync(conversationId, CancellationToken.None);

        await Assert.That(latest).IsNotNull();
        await Assert.That(latest!.Id).IsEqualTo("c2");
        await Assert.That(latest.ToRowId).IsEqualTo(20);
        await Assert.That(latest.Model).IsEqualTo("summary-model");
    }

    [Test]
    public async Task Compactor_PersistsACheckpointAndTheProjectionRestoresIt()
    {
        var conversationId = Guid.NewGuid();
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();
        var store = new SqliteAgentCompactionStore(fixture.Database);
        var compactor = new AgentContextCompactor(new FakeSummarizer(), store: store);

        var messages = new List<AgentMessage>();
        for (var index = 0; index < 10; index++)
        {
            messages.Add(AgentMessage.User("шаг " + index + " " + new string('я', 4000)));
            messages.Add(AgentMessage.Assistant("ответ " + index));
        }

        var result = await compactor.CompactAsync(
            new AgentCompactionRequest
            {
                Messages = messages,
                ContextBudgetTokens = 10_000,
                Threshold = 0.3,
                KeepRecentMessages = 2,
                SessionId = conversationId
            },
            CancellationToken.None);

        await Assert.That(result.Summarized).IsTrue();
        var checkpoint = await store.GetLatestAsync(conversationId, CancellationToken.None);
        await Assert.That(checkpoint).IsNotNull();
        await Assert.That(checkpoint!.ToRowId).IsEqualTo(messages.Count - 2);

        // Projection used by the orchestrator after a restart.
        var tail = messages.Skip(checkpoint.ToRowId).ToList();
        await Assert.That(tail.Count).IsEqualTo(2);
        await Assert.That(checkpoint.Summary).Contains("CHECKPOINT");
    }

    [Test]
    public async Task Compactor_PersistsACheckpointThatValidatesAgainstTheRealStore()
    {
        var conversationId = Guid.NewGuid();
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();

        var conversations = new SqliteAgentConversationStore(fixture.Database);
        await conversations.CreateConversationAsync(
            new AgentConversationHeader
            {
                Id = conversationId,
                Title = "compaction",
                DocumentId = "doc"
            },
            CancellationToken.None);

        for (var index = 0; index < 10; index++)
        {
            await conversations.AppendMessageAsync(
                conversationId,
                AgentMessage.User("шаг " + index + " " + new string('я', 6000)),
                CancellationToken.None);
        }

        var messages = await conversations.GetMessagesAsync(
            conversationId, CancellationToken.None);
        var store = new SqliteAgentCompactionStore(fixture.Database);
        var compactor = new AgentContextCompactor(new FakeSummarizer(), store: store);

        var result = await compactor.CompactAsync(
            new AgentCompactionRequest
            {
                Messages = messages,
                ContextBudgetTokens = 10_000,
                Threshold = 0.3,
                KeepRecentMessages = 2,
                BaseRowId = 0,
                RawHistory = messages,
                SessionId = conversationId
            },
            CancellationToken.None);

        await Assert.That(result.Summarized).IsTrue();
        var checkpoint = await store.GetLatestAsync(conversationId, CancellationToken.None);
        await Assert.That(checkpoint).IsNotNull();
        await Assert.That(AgentCompactionHash.Validate(checkpoint!, messages)).IsTrue();
    }

    [Test]
    public async Task Compactor_HonoursCancellation()
    {
        var compactor = new AgentContextCompactor(new SlowSummarizer());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var messages = new List<AgentMessage>
        {
            AgentMessage.User(new string('я', 40_000))
        };

        var cancelled = false;
        try
        {
            await compactor.CompactAsync(
                new AgentCompactionRequest
                {
                    Messages = messages,
                    ContextBudgetTokens = 10_000,
                    Threshold = 0.3,
                    KeepRecentMessages = 1,
                    SessionId = Guid.NewGuid()
                },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        await Assert.That(cancelled).IsTrue();
    }

    private sealed class FakeSummarizer : IAgentSummarizer
    {
        public Task<string> SummarizeAsync(
            string input,
            int maxOutputTokens,
            CancellationToken ct) =>
            Task.FromResult("CHECKPOINT summary");
    }

    private sealed class SlowSummarizer : IAgentSummarizer
    {
        public async Task<string> SummarizeAsync(
            string input,
            int maxOutputTokens,
            CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return "late";
        }
    }

    private sealed class FakeAgentEvent : IAgentAwaitableEvent
    {
        public Task<T> RaiseAsync<T>(
            Func<object, T> funcWithApp,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(funcWithApp(this));
        }

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            actionWithApp(this);
            return Task.CompletedTask;
        }

        public void ProcessQueue(object revitApp)
        {
        }

        public void Initialize(Action onRaise)
        {
        }
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "smartcon-agent-compaction-tests",
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
