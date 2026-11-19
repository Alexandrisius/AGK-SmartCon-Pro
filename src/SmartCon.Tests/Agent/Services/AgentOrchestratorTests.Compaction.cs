using Xunit;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed partial class AgentOrchestratorTests
{
    private const int CompactionContextWindow = 50_000;
    private const int HugeToolDescriptionLength = 400_000;

    [Fact]
    public async Task RunTurn_Compaction_EstimatesSystemAndToolsToo()
    {
        var toolMessages = await RunTurnWithCompaction();

        Assert.Equal(5, toolMessages.Count);
        Assert.Contains(toolMessages, message =>
            (message.Content ?? string.Empty).Contains("\"compacted\":true", StringComparison.Ordinal));
        Assert.Contains(toolMessages, message =>
            (message.Content ?? string.Empty).Contains("original-result-", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<AgentMessage>> RunTurnWithCompaction()
    {
        var store = new InMemoryConversationStore();
        for (var index = 0; index < 5; index++)
        {
            store.Messages.Add(AgentMessage.Assistant(
                null,
                new[]
                {
                    new AgentToolCall
                    {
                        Id = "hist_" + index,
                        Name = "get_test",
                        ArgumentsJson = "{}"
                    }
                }));
            store.Messages.Add(
                AgentMessage.ToolResult("hist_" + index, "original-result-" + index));
        }

        var tool = new CountingTool(
            name: "get_test",
            description: new string('x', HugeToolDescriptionLength));
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("done");
        var settings = new AgentLlmSettings
        {
            Model = "test-model",
            ContextWindowTokens = CompactionContextWindow,
            ContextThreshold = 0.7
        };
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, settings: settings);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        return llm.Requests[0].Messages
            .Where(message => message.Role == AgentRole.Tool)
            .ToList();
    }

    [Fact]
    public async Task RunTurn_DoesNotTrimLongHistory()
    {
        var store = new InMemoryConversationStore();
        store.Messages.Add(AgentMessage.User("FIRST-INSTRUCTION"));
        for (var index = 0; index < 160; index++)
        {
            store.Messages.Add(AgentMessage.User("шаг " + index));
            store.Messages.Add(AgentMessage.Assistant("ответ " + index));
        }

        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("done");
        var settings = new AgentLlmSettings
        {
            Model = "test-model",
            ContextWindowTokens = 2_000_000,
            ContextThreshold = 0.7
        };
        var orchestrator = CreateOrchestrator(
            llm, CreateExecutor(registry), registry, store, settings: settings);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "продолжай",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var request = llm.Requests[0].Messages;
        Assert.Contains(
            request,
            message => message.Role == AgentRole.User
                && (message.Content ?? string.Empty).Contains(
                    "FIRST-INSTRUCTION", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTurn_ProjectsOldToolResultsAndReportsContextStatus()
    {
        var store = new InMemoryConversationStore();
        for (var index = 0; index < 10; index++)
        {
            store.Messages.Add(AgentMessage.Assistant(
                null,
                new[]
                {
                    new AgentToolCall
                    {
                        Id = "hist_" + index,
                        Name = "get_test",
                        ArgumentsJson = "{}"
                    }
                }));
            store.Messages.Add(AgentMessage.ToolResult(
                "hist_" + index,
                "{\"success\":true,\"message\":\"original-result-" + index +
                "\",\"affectedCount\":1,\"data\":{\"items\":\"" +
                new string('x', 20_000) + "\"}}"));
        }

        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("done");
        var settings = new AgentLlmSettings
        {
            Model = "test-model",
            // The projection gate runs at 75 % of the compaction limit: with a
            // 500k window this ~57k history stays verbatim (by design). A small
            // window puts it above the gate and exercises the projection path.
            ContextWindowTokens = 80_000,
            ContextThreshold = 0.7
        };
        var orchestrator = CreateOrchestrator(
            llm,
            CreateExecutor(registry),
            registry,
            store,
            settings: settings,
            contextCompactor: new AgentContextCompactor(new EvalSummarizer()));

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "продолжай",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var request = llm.Requests[0].Messages;
        Assert.Contains(
            request,
            message => message.Role == AgentRole.Tool
                && (message.Content ?? string.Empty).Contains(
                    "\"compacted\":true", StringComparison.Ordinal));
        Assert.Contains(
            request,
            message => message.Role == AgentRole.Tool
                && (message.Content ?? string.Empty).Contains(
                    "original-result-", StringComparison.Ordinal));

        var status = request[request.Count - 1];
        Assert.Equal(AgentRole.System, status.Role);
        Assert.Contains("<context_status", status.Content, StringComparison.Ordinal);
        Assert.Contains("compacted=\"masking\"", status.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_IgnoresCheckpointWithMismatchedHash()
    {
        var store = new InMemoryConversationStore();
        store.Messages.Add(AgentMessage.User("FIRST-INSTRUCTION"));
        store.Messages.Add(AgentMessage.Assistant("ответ"));
        store.Messages.Add(AgentMessage.User("ещё"));

        var compactionStore = new EvalCompactionStore();
        compactionStore.Saved.Add(new AgentCompactionRecord
        {
            Id = "stale",
            SessionId = Guid.NewGuid(),
            ToRowId = 2,
            Summary = "STALE-CHECKPOINT",
            SourcePrefixHash = "deadbeef",
            TokensBefore = 100,
            TokensAfter = 10
        });

        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("done");
        var settings = new AgentLlmSettings
        {
            Model = "test-model",
            ContextWindowTokens = 2_000_000,
            ContextThreshold = 0.7
        };
        var orchestrator = CreateOrchestrator(
            llm,
            CreateExecutor(registry),
            registry,
            store,
            settings: settings,
            compactionStore: compactionStore);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "продолжай",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var request = llm.Requests[0].Messages;
        Assert.DoesNotContain(
            request,
            message => (message.Content ?? string.Empty).Contains(
                "<conversation_checkpoint>", StringComparison.Ordinal));
        Assert.Contains(
            request,
            message => (message.Content ?? string.Empty).Contains(
                "FIRST-INSTRUCTION", StringComparison.Ordinal));
    }
}
