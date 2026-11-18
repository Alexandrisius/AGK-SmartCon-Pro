using Xunit;
using System.Text.Json;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;

namespace SmartCon.Tests.Agent.Services;

public sealed partial class AgentOrchestratorTests
{
    [Fact]
    public async Task RunTurn_ToolCallThenAnswer_ExecutesToolAndAppendsMessages()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);

        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "call_1",
                    Name = "get_test",
                    ArgumentsJson = "{}"
                }
            },
            "thinking");
        llm.EnqueueAssistant("Готово");

        var orchestrator = CreateOrchestrator(llm, executor, registry, store);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("Готово", result.FinalText);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(4, store.Messages.Count);
        Assert.Equal(2, llm.Requests.Count);

        var secondRequest = llm.Requests[1].Messages;
        Assert.Contains(secondRequest, message =>
            message.Role == AgentRole.Tool && message.ToolCallId == "call_1");

        var replayedAssistant = secondRequest.First(message => message.Role == AgentRole.Assistant);
        Assert.Equal("thinking", replayedAssistant.ReasoningContent);
        Assert.Single(replayedAssistant.ToolCalls);
    }

    [Fact]
    public async Task RunTurn_CancelledToken_Throws()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var orchestrator = CreateOrchestrator(
            new FakeAgentLlmClient(),
            executor,
            registry,
            store);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.RunTurnAsync(
                Guid.NewGuid(),
                "привет",
                Array.Empty<AgentAttachment>(),
                null,
                cts.Token));
    }

    [Fact]
    public async Task RunTurn_ToolFailure_IsReportedBackToTheModel()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool(ToolResult.Fail("boom", "Tool exploded"));
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);

        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = "call_1", Name = "get_test", ArgumentsJson = "{}" } });
        llm.EnqueueAssistant("Не удалось");

        var orchestrator = CreateOrchestrator(llm, executor, registry, store);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("Не удалось", result.FinalText);
        var toolMessage = llm.Requests[1].Messages.Last(message => message.Role == AgentRole.Tool);
        Assert.NotNull(toolMessage.Content);
        Assert.Contains("boom", toolMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_HistoryWithOrphanedToolCall_BackfillsCancelledResult()
    {
        var store = new InMemoryConversationStore();
        store.Messages.Add(AgentMessage.Assistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "lost_call",
                    Name = "list_views",
                    ArgumentsJson = "{}"
                }
            },
            "thinking"));
        store.Messages.Add(AgentMessage.User("продолжай"));

        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("Ок");
        var orchestrator = CreateOrchestrator(llm, executor, registry, store);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "продолжай",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("Ок", result.FinalText);
        var replayed = llm.Requests[0].Messages;
        var toolMessage = Assert.Single(
            replayed,
            message => message.Role == AgentRole.Tool && message.ToolCallId == "lost_call");
        Assert.NotNull(toolMessage.Content);
        Assert.Contains("cancelled", toolMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_FindTools_ExpandsCatalogForNextRound()
    {
        var store = new InMemoryConversationStore();
        var findTools = new CountingTool(name: "find_tools");
        var rename = new CountingTool(name: "rename_views");
        var registry = new AgentToolRegistry(new IAgentTool[] { findTools, rename });
        var executor = CreateExecutor(registry);
        var router = new RecordingToolRouter
        {
            Result = new ToolRoutingDecision { Tools = new[] { findTools.Definition } }
        };
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "call_1",
                    Name = "find_tools",
                    ArgumentsJson = "{\"query\":\"rename views\"}"
                }
            });
        llm.EnqueueAssistant("done");

        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, toolRouter: router);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "наведи порядок в именах видов",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal(2, llm.Requests.Count);
        Assert.DoesNotContain(
            llm.Requests[0].Tools, tool => tool.Name == "rename_views");
        Assert.Contains(
            llm.Requests[1].Tools, tool => tool.Name == "rename_views");
        Assert.Equal(1, findTools.Calls);
    }

    [Fact]
    public async Task RunTurn_ActiveDocumentChanged_ResetsApprovalModeToConfirmWrites()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var session = new AgentSessionContext
        {
            DocumentId = "doc-a",
            ApprovalMode = AgentApprovalMode.AutoApproveSession
        };
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("ok");

        var orchestrator = CreateOrchestrator(
            llm,
            executor,
            registry,
            store,
            session,
            new StubProjectContextProvider("doc-b"));

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal(AgentApprovalMode.ConfirmWrites, session.ApprovalMode);
        Assert.Equal("doc-b", session.DocumentId);
    }

    private static AgentToolExecutor CreateExecutor(IToolRegistry registry)
    {
        return new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext(),
            new AutoApproveConfirmation());
    }

    private sealed class AutoApproveConfirmation : IAgentConfirmationService
    {
        public Task<AgentConfirmationResult> RequestAsync(
            AgentConfirmationRequest request,
            CancellationToken ct) =>
            Task.FromResult(AgentConfirmationResult.Approved);
    }

    private static AgentOrchestrator CreateOrchestrator(
        FakeAgentLlmClient llm,
        IToolExecutor executor,
        IToolRegistry registry,
        IAgentConversationStore store,
        AgentSessionContext? session = null,
        IAgentProjectContextProvider? projectContext = null,
        IAgentToolCallStore? toolCallStore = null,
        IToolRouter? toolRouter = null,
        IAgentUsageStore? usageStore = null,
        AgentLlmSettings? settings = null,
        IAgentWorkingStateStore? workingStateStore = null)
    {
        return new AgentOrchestrator(
            llm,
            executor,
            registry,
            store,
            new NullAuditLog(),
            projectContext ?? new StubProjectContextProvider(),
            settings ?? new AgentLlmSettings { Model = "test-model" },
            session ?? new AgentSessionContext(),
            new AgentPromptBuilder(),
            toolCallStore,
            usageStore,
            toolRouter,
            workingStateStore);
    }

    private sealed class CountingTool : IAgentTool
    {
        private readonly ToolResult _result;

        public CountingTool(
            ToolResult? result = null,
            string name = "get_test",
            string? description = null)
        {
            _result = result ?? ToolResult.Ok(null, "ok");
            Definition = new AgentToolDefinition
            {
                Name = name,
                Description = description ?? name + " test tool",
                Risk = AgentToolRisk.Read,
                InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}"
            };
        }

        public int Calls { get; private set; }

        public AgentToolDefinition Definition { get; }

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class ImmediateAwaitableEvent : IAgentAwaitableEvent
    {
        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(funcWithApp(new object()));
        }

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            actionWithApp(new object());
            return Task.CompletedTask;
        }

        public void ProcessQueue(object revitApp)
        {
        }

        public void Initialize(Action onRaise)
        {
        }
    }

    private sealed class StubProjectContextProvider : IAgentProjectContextProvider
    {
        private readonly string _documentId;

        public StubProjectContextProvider(string documentId = "doc")
        {
            _documentId = documentId;
        }

        public Task<AgentProjectContext> GetAsync(CancellationToken ct) => Task.FromResult(
            new AgentProjectContext
            {
                DocumentId = _documentId,
                Title = "Test document",
                RevitVersion = "2025"
            });
    }

    private sealed class CapturingToolCallStore : IAgentToolCallStore
    {
        public List<AgentToolCallRecord> Records { get; } = new();

        public Task RecordAsync(AgentToolCallRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingUsageStore : IAgentUsageStore
    {
        public List<AgentUsageRecord> Records { get; } = new();

        public Task RecordAsync(AgentUsageRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryWorkingStateStore : IAgentWorkingStateStore
    {
        private readonly Dictionary<Guid, WorkingState> _states = new();

        public List<WorkingState> Saved { get; } = new();

        public Task<WorkingState?> LoadAsync(Guid conversationId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                _states.TryGetValue(conversationId, out var state) ? state : null);
        }

        public Task SaveAsync(Guid conversationId, WorkingState state, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _states[conversationId] = state;
            Saved.Add(state);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingWorkingStateStore : IAgentWorkingStateStore
    {
        public int Calls { get; private set; }

        public Task<WorkingState?> LoadAsync(Guid conversationId, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("The working state store must stay untouched");
        }

        public Task SaveAsync(Guid conversationId, WorkingState state, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("The working state store must stay untouched");
        }
    }

    private sealed class NullAuditLog : IAgentAuditLog
    {
        public Task WriteAsync(AgentAuditEntry entry, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class InMemoryConversationStore : IAgentConversationStore
    {
        public List<AgentMessage> Messages { get; } = new();

        public Task<Guid> CreateConversationAsync(AgentConversationHeader header, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid());

        public Task<IReadOnlyList<AgentConversationHeader>> ListConversationsAsync(
            string documentId,
            int limit,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentConversationHeader>>(
                Array.Empty<AgentConversationHeader>());

        public Task<AgentConversationHeader?> GetConversationAsync(
            Guid conversationId,
            CancellationToken ct) =>
            Task.FromResult<AgentConversationHeader?>(null);

        public Task AppendMessageAsync(Guid conversationId, AgentMessage message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(
            Guid conversationId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentMessage>>(Messages.ToList());

        public Task UpdateTitleAsync(Guid conversationId, string title, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
