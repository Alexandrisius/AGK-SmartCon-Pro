using Xunit;
using System.Text.Json;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed partial class AgentOrchestratorTests
{
    [Fact]
    public async Task RunTurn_WorkingStateDisabled_DoesNotInjectOrTouchStore()
    {
        var store = new InMemoryConversationStore();
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("ok");
        var workingStore = new ThrowingWorkingStateStore();

        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, workingStateStore: workingStore);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var user = llm.Requests[0].Messages.Last(message => message.Role == AgentRole.User);
        Assert.Equal("привет", user.Content);
        Assert.DoesNotContain("<turn_context>", user.Content);
        Assert.Equal(0, workingStore.Calls);
    }

    [Fact]
    public async Task RunTurn_WorkingStateEnabled_InjectsTurnContextButPersistsOriginalUserText()
    {
        const string userText = "Переименуй виды, не трогай листы";
        var projectContext = new StubProjectContextProvider("doc-ws");

        var offRegistry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var offLlm = new FakeAgentLlmClient();
        offLlm.EnqueueAssistant("ok");
        var offOrchestrator = CreateOrchestrator(
            offLlm,
            CreateExecutor(offRegistry),
            offRegistry,
            new InMemoryConversationStore(),
            projectContext: projectContext,
            settings: new AgentLlmSettings
            {
                Model = "test-model",
                ToolCatalogV2 = false,
                ContextCompactionV2 = false,
                WorkingStateEnabled = false
            });

        await offOrchestrator.RunTurnAsync(
            Guid.NewGuid(),
            userText,
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var onStore = new InMemoryConversationStore();
        var onRegistry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var onLlm = new FakeAgentLlmClient();
        onLlm.EnqueueAssistant("ok");
        var workingStore = new InMemoryWorkingStateStore();
        var onOrchestrator = CreateOrchestrator(
            onLlm,
            CreateExecutor(onRegistry),
            onRegistry,
            onStore,
            projectContext: projectContext,
            settings: new AgentLlmSettings
            {
                Model = "test-model",
                WorkingStateEnabled = true,
                ToolCatalogV2 = false,
                ContextCompactionV2 = false
            },
            workingStateStore: workingStore);

        await onOrchestrator.RunTurnAsync(
            Guid.NewGuid(),
            userText,
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var offUser = offLlm.Requests[0].Messages.Last(
            message => message.Role == AgentRole.User);
        var onUser = onLlm.Requests[0].Messages.Last(
            message => message.Role == AgentRole.User);

        Assert.Equal(userText, offUser.Content);
        Assert.StartsWith("<turn_context>", onUser.Content);
        Assert.Contains("goal: " + userText, onUser.Content);
        Assert.EndsWith("\n\n" + userText, onUser.Content);

        Assert.Equal(
            offLlm.Requests[0].Messages[0].Content,
            onLlm.Requests[0].Messages[0].Content);

        Assert.Contains(
            onStore.Messages,
            message => message.Role == AgentRole.User && message.Content == userText);
        Assert.DoesNotContain(
            onStore.Messages,
            message => message.Role == AgentRole.User
                && (message.Content ?? string.Empty).Contains(
                    "<turn_context>", StringComparison.Ordinal));

        var saved = Assert.Single(workingStore.Saved);
        Assert.Equal(userText, saved.Goal);
    }

    [Fact]
    public async Task RunTurn_WorkingStateEnabled_RestoresGoalFromStoreOnRestart()
    {
        var conversationId = Guid.NewGuid();
        var conversationStore = new InMemoryConversationStore();
        var workingStore = new InMemoryWorkingStateStore();
        var projectContext = new StubProjectContextProvider("doc-ws");
        var settings = new AgentLlmSettings
        {
            Model = "test-model",
            WorkingStateEnabled = true
        };

        var firstRegistry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var firstLlm = new FakeAgentLlmClient();
        firstLlm.EnqueueAssistant("ok");
        var first = CreateOrchestrator(
            firstLlm,
            CreateExecutor(firstRegistry),
            firstRegistry,
            conversationStore,
            projectContext: projectContext,
            settings: settings,
            workingStateStore: workingStore);

        await first.RunTurnAsync(
            conversationId,
            "Почини имена видов",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var secondRegistry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var secondLlm = new FakeAgentLlmClient();
        secondLlm.EnqueueAssistant("ok");
        var second = CreateOrchestrator(
            secondLlm,
            CreateExecutor(secondRegistry),
            secondRegistry,
            conversationStore,
            projectContext: projectContext,
            settings: settings,
            workingStateStore: workingStore);

        await second.RunTurnAsync(
            conversationId,
            "продолжай",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var user = secondLlm.Requests[0].Messages.Last(
            message => message.Role == AgentRole.User);
        Assert.Contains("goal: Почини имена видов", user.Content);
        Assert.EndsWith("\n\nпродолжай", user.Content);
    }

    [Fact]
    public async Task RunTurn_WorkingStateEnabled_RecordsChangeSetFromToolResult()
    {
        var store = new InMemoryConversationStore();
        var payload = JsonSerializer.SerializeToElement(new
        {
            changeSetId = "cs-42",
            changed = new[] { new { id = 7 } }
        });
        var tool = new CountingTool(ToolResult.Ok(payload, "applied", 1));
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
            });
        llm.EnqueueAssistant("done");
        var workingStore = new InMemoryWorkingStateStore();

        var orchestrator = CreateOrchestrator(
            llm,
            executor,
            registry,
            store,
            settings: new AgentLlmSettings
            {
                Model = "test-model",
                WorkingStateEnabled = true,
                ToolCatalogV2 = false,
                ContextCompactionV2 = false
            },
            workingStateStore: workingStore);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "примени",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var saved = Assert.Single(workingStore.Saved);
        Assert.Equal(new[] { "cs-42" }, saved.ChangeSetIds);
        Assert.Equal(new[] { "7" }, saved.TouchedElementIds);
    }

    [Fact]
    public async Task RunTurn_ConversationChanged_ResetsWorkingStateAndDoesNotLeakOldGoal()
    {
        var firstConversation = Guid.NewGuid();
        var secondConversation = Guid.NewGuid();
        var conversationStore = new InMemoryConversationStore();
        var workingStore = new InMemoryWorkingStateStore();
        var session = new AgentSessionContext();
        var settings = new AgentLlmSettings
        {
            Model = "test-model",
            WorkingStateEnabled = true
        };
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("ok");
        llm.EnqueueAssistant("ok");
        var orchestrator = CreateOrchestrator(
            llm,
            executor,
            registry,
            conversationStore,
            session,
            settings: settings,
            workingStateStore: workingStore);

        await orchestrator.RunTurnAsync(
            firstConversation,
            "Почини имена видов",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        await orchestrator.RunTurnAsync(
            secondConversation,
            "Другая задача",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var secondUser = llm.Requests[1].Messages.Last(
            message => message.Role == AgentRole.User);
        Assert.Contains("goal: Другая задача", secondUser.Content);
        Assert.DoesNotContain("Почини имена видов", secondUser.Content);

        Assert.Equal("Другая задача", session.WorkingState!.Goal);
        Assert.Equal(2, workingStore.Saved.Count);
        Assert.Equal("Почини имена видов", workingStore.Saved[0].Goal);
        Assert.Equal("Другая задача", workingStore.Saved[1].Goal);
    }
}
