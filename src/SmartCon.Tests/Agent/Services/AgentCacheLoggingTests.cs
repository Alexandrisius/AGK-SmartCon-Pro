using System.Globalization;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentCacheLoggingTests
{
    [Fact]
    public void Build_ContainsEveryCacheField()
    {
        var usage = new AgentUsage
        {
            PromptTokens = 1200,
            CompletionTokens = 40,
            CachedTokens = 900,
            CacheWriteTokens = 120,
            TotalTokens = 1240,
            CostUsd = 0.0123m
        };

        var line = AgentCacheLog.Build(2, "req-7", usage, "static123", "tools456");

        Assert.Contains("round=2", line, StringComparison.Ordinal);
        Assert.Contains("request_id=req-7", line, StringComparison.Ordinal);
        Assert.Contains("prompt_tokens=1200", line, StringComparison.Ordinal);
        Assert.Contains("cached_tokens=900", line, StringComparison.Ordinal);
        Assert.Contains("cache_write_tokens=120", line, StringComparison.Ordinal);
        Assert.Contains("cost_usd=0.012300", line, StringComparison.Ordinal);
        Assert.Contains("static_hash=static123", line, StringComparison.Ordinal);
        Assert.Contains("tool_set_hash=tools456", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CostUsesInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            var line = AgentCacheLog.Build(
                1,
                null,
                new AgentUsage { CostUsd = 1.5m },
                "s",
                "t");

            Assert.Contains("cost_usd=1.500000", line, StringComparison.Ordinal);
            Assert.DoesNotContain("1,500000", line, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Build_MissingRequestIdAndCost_AreMarkedExplicitly()
    {
        var line = AgentCacheLog.Build(3, null, AgentUsage.Empty, "s", "t");

        Assert.Contains("request_id=(none)", line, StringComparison.Ordinal);
        Assert.Contains("cost_usd=(none)", line, StringComparison.Ordinal);
        Assert.Contains("cached_tokens=0", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_AfterCompaction_WritesCompactionIdOnUsageRecord()
    {
        var record = new AgentCompactionRecord
        {
            Id = "chk-42",
            SessionId = Guid.NewGuid(),
            ToRowId = 4,
            Summary = "summary"
        };
        var usageStore = new UsageCapture();
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("done");

        var orchestrator = Create(llm, usageStore, new CheckpointCompactor(record));

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("chk-42", Assert.Single(usageStore.Records).CompactionId);
    }

    [Fact]
    public async Task RunTurn_WithoutCompaction_LeavesCompactionIdEmpty()
    {
        var usageStore = new UsageCapture();
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("done");

        var orchestrator = Create(llm, usageStore, new CheckpointCompactor(null));

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Null(Assert.Single(usageStore.Records).CompactionId);
    }

    private static AgentOrchestrator Create(
        FakeAgentLlmClient llm,
        IAgentUsageStore usageStore,
        IAgentContextCompactor compactor)
    {
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var executor = new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext(),
            new AutoApproveConfirmation());

        return new AgentOrchestrator(
            llm,
            executor,
            registry,
            new InMemoryConversationStore(),
            new NullAuditLog(),
            new StubProjectContextProvider(),
            new AgentLlmSettings { Model = "test-model" },
            new AgentSessionContext(),
            new AgentPromptBuilder(),
            usageStore: usageStore,
            contextCompactor: compactor);
    }

    private sealed class CheckpointCompactor : IAgentContextCompactor
    {
        private readonly AgentCompactionRecord? _record;

        public CheckpointCompactor(AgentCompactionRecord? record) => _record = record;

        public Task<AgentCompactionResult> ProjectAsync(
            AgentCompactionRequest request,
            CancellationToken ct)
        {
            if (_record is null)
                return Task.FromResult(Unchanged(request));

            return Task.FromResult(new AgentCompactionResult
            {
                Messages = request.Messages.ToList(),
                Summarized = true,
                Record = _record
            });
        }

        public Task<AgentCompactionResult> CompactAsync(
            AgentCompactionRequest request,
            CancellationToken ct) =>
            Task.FromResult(Unchanged(request));

        private static AgentCompactionResult Unchanged(AgentCompactionRequest request) =>
            new() { Messages = request.Messages.ToList() };
    }

    private sealed class UsageCapture : IAgentUsageStore
    {
        public List<AgentUsageRecord> Records { get; } = new();

        public Task RecordAsync(AgentUsageRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<decimal> GetSessionCostAsync(Guid sessionId, CancellationToken ct) =>
            Task.FromResult(0m);

        public Task<AgentContextSnapshot?> GetLastContextAsync(
            Guid sessionId,
            CancellationToken ct) =>
            Task.FromResult<AgentContextSnapshot?>(null);

        public Task<AgentSessionMetrics?> GetSessionMetricsAsync(
            Guid sessionId,
            CancellationToken ct) =>
            Task.FromResult<AgentSessionMetrics?>(null);
    }

    private sealed class AutoApproveConfirmation : IAgentConfirmationService
    {
        public Task<AgentConfirmationResult> RequestAsync(
            AgentConfirmationRequest request,
            CancellationToken ct) =>
            Task.FromResult(AgentConfirmationResult.Approved);
    }

    private sealed class NullAuditLog : IAgentAuditLog
    {
        public Task WriteAsync(AgentAuditEntry entry, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class StubProjectContextProvider : IAgentProjectContextProvider
    {
        public Task<AgentProjectContext> GetAsync(CancellationToken ct) => Task.FromResult(
            new AgentProjectContext
            {
                DocumentId = "doc",
                Title = "Test document",
                RevitVersion = "2025"
            });
    }

    private sealed class InMemoryConversationStore : IAgentConversationStore
    {
        private readonly List<AgentMessage> _messages = new();

        public Task<Guid> CreateConversationAsync(
            AgentConversationHeader header,
            CancellationToken ct) =>
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

        public Task AppendMessageAsync(
            Guid conversationId,
            AgentMessage message,
            CancellationToken ct)
        {
            _messages.Add(message.SessionIndex > 0
                ? message
                : message.WithSessionIndex(_messages.Count + 1));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(
            Guid conversationId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentMessage>>(_messages.ToList());

        public Task UpdateTitleAsync(Guid conversationId, string title, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}