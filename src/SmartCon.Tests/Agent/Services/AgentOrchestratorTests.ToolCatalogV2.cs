using System.Text.Json;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed partial class AgentOrchestratorTests
{
    private static AgentLlmSettings V2Settings() =>
        new() { Model = "test-model" };

    private static List<IAgentTool> CoreTools() =>
        AgentToolPacks.CoreToolNames
            .Select(name => (IAgentTool)new CountingTool(name: name))
            .ToList();

    [Fact]
    public async Task RunTurn_ToolCatalogV2_StartsWithCoreAndSystemOnly()
    {
        var store = new InMemoryConversationStore();
        var tools = CoreTools();
        tools.Add(new CountingTool(name: "set_view_crop"));
        tools.Add(new CountingTool(name: "set_view_visibility"));
        tools.Add(new CountingTool(name: "list_view_templates"));
        var registry = new AgentToolRegistry(tools);
        var session = new AgentSessionContext();
        var executor = CreateExecutor(registry, session);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("ok");
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, session, settings: V2Settings());

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "включи область подрезки у вида",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var sent = llm.Requests[0].Tools;
        foreach (var core in AgentToolPacks.CoreToolNames)
            Assert.Contains(sent, tool => tool.Name == core);

        // No retrieval runs on the first message: the model must find pack tools
        // itself, so a crop query does not preload set_view_crop.
        Assert.DoesNotContain(sent, tool => tool.Name == "set_view_crop");
        Assert.DoesNotContain(sent, tool => tool.Name == "set_view_visibility");
        Assert.DoesNotContain(sent, tool => tool.Name == "list_view_templates");
        Assert.Equal(AgentToolPacks.CoreToolNames.Count, sent.Count);
    }

    [Fact]
    public async Task RunTurn_ToolCatalogV2_FindToolsDoesNotExpandThePayload()
    {
        var store = new InMemoryConversationStore();
        var tools = CoreTools();
        tools.Add(new CountingTool(name: "set_view_crop"));
        var registry = new AgentToolRegistry(tools);
        var session = new AgentSessionContext();
        var executor = CreateExecutor(registry, session);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "call_1",
                    Name = "find_tools",
                    ArgumentsJson = "{\"query\":\"подрезка\"}"
                }
            });
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, session, settings: V2Settings());

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal(2, llm.Requests.Count);
        Assert.Equal(llm.Requests[0].Tools.Count, llm.Requests[1].Tools.Count);
        Assert.DoesNotContain(llm.Requests[1].Tools, tool => tool.Name == "set_view_crop");
    }

    [Fact]
    public async Task RunTurn_ToolCatalogV2_LoadToolPackAppliesAtTheNextRound()
    {
        var store = new InMemoryConversationStore();
        var session = new AgentSessionContext();
        var loadTool = new LoadToolPackTool(session);
        var tools = CoreTools();
        tools.Add(new CountingTool(name: "set_view_crop"));
        tools.Add(new CountingTool(name: "set_view_range"));
        tools.Add(new CountingTool(name: "set_view_visibility"));
        tools.Add(new CountingTool(name: "set_view_parameters"));
        tools.Add(loadTool);
        var registry = new AgentToolRegistry(tools, null, loadTool);
        var executor = CreateExecutor(registry, session);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "call_1",
                    Name = "load_tool_pack",
                    ArgumentsJson = "{\"pack\":\"view_settings\"}"
                }
            });
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, session, settings: V2Settings());

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal(2, llm.Requests.Count);
        Assert.DoesNotContain(llm.Requests[0].Tools, tool => tool.Name == "set_view_crop");
        Assert.Contains(llm.Requests[1].Tools, tool => tool.Name == "get_view_settings");
        Assert.Contains(llm.Requests[1].Tools, tool => tool.Name == "set_view_crop");
        Assert.Contains(llm.Requests[1].Tools, tool => tool.Name == "set_view_range");
        Assert.Contains(llm.Requests[1].Tools, tool => tool.Name == "set_view_visibility");
        Assert.Contains(llm.Requests[1].Tools, tool => tool.Name == "set_view_parameters");
        Assert.Empty(session.PendingToolNames);
        // get_view_settings is part of the research core now, so it is reported as
        // alreadyLoaded; only the four write setters enter the loaded set.
        Assert.Equal(4, session.LoadedToolNames.Count);
    }

    [Fact]
    public async Task RunTurn_ToolCatalogV2_LoadedToolsNeverFallOutOfTheRequest()
    {
        // Regression for the 2026-11-19 sessions: 21 tools were "loaded", the
        // request carried only the first six of them, and the model could not
        // call undo_last / list_sheets at all. A tool that was loaded or used
        // must stay callable on every following turn.
        var store = new InMemoryConversationStore();
        var session = new AgentSessionContext();
        var tools = CoreTools();
        for (var index = 0; index < 10; index++)
            tools.Add(new CountingTool(name: "filler_" + index));
        tools.Add(new CountingTool(name: "undo_last"));
        var registry = new AgentToolRegistry(tools);
        var executor = CreateExecutor(registry, session);

        session.TouchTool("undo_last");
        for (var index = 0; index < 10; index++)
            session.TouchTool("filler_" + index);

        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, session, settings: V2Settings());

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var sent = Assert.Single(llm.Requests).Tools;
        Assert.Contains(sent, tool => tool.Name == "undo_last");
        Assert.Contains(sent, tool => tool.Name == "filler_9");
    }

    [Fact]
    public async Task RunTurn_ToolCatalogV2_UserTextDoesNotPullDomainTools()
    {
        // The turn text is not a routing query any more: a request mentions sheets and
        // schedules, but their tools stay out until load_tool_pack enables them.
        var store = new InMemoryConversationStore();
        var session = new AgentSessionContext();
        var tools = CoreTools();
        for (var index = 0; index < 10; index++)
            tools.Add(new CountingTool(name: "filler_" + index));
        tools.Add(new CountingTool(
            name: "list_sheets", description: "Список листов проекта: номер, имя, комплект"));
        tools.Add(new CountingTool(
            name: "list_schedules", description: "Список спецификаций и ведомостей"));
        var registry = new AgentToolRegistry(tools);
        var executor = CreateExecutor(registry, session);

        for (var index = 0; index < 10; index++)
            session.TouchTool("filler_" + index);

        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, session, settings: V2Settings());

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "продолжай и проверь все листы и спецификации",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var sent = Assert.Single(llm.Requests).Tools;
        Assert.Contains(sent, tool => tool.Name == "filler_0");
        Assert.DoesNotContain(sent, tool => tool.Name == "list_sheets");
        Assert.DoesNotContain(sent, tool => tool.Name == "list_schedules");
    }

    [Fact]
    public async Task RunTurn_ToolCatalogV2_KeepsDefinitionsFixedWithinRound()
    {
        var store = new InMemoryConversationStore();
        var session = new AgentSessionContext();
        var loadTool = new LoadToolPackTool(session);
        var capture = new ActiveToolsCapturingTool("get_project_overview");
        var tools = CoreTools()
            .Where(tool => tool.Definition.Name != "get_project_overview")
            .ToList();
        tools.Add(capture);
        tools.Add(new CountingTool(name: "set_view_crop"));
        tools.Add(loadTool);
        var registry = new AgentToolRegistry(tools, null, loadTool);
        var executor = CreateExecutor(registry, session);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "call_1",
                    Name = "load_tool_pack",
                    ArgumentsJson = "{\"pack\":\"view_settings\"}"
                },
                new AgentToolCall
                {
                    Id = "call_2",
                    Name = "get_project_overview",
                    ArgumentsJson = "{}"
                }
            });
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, session, settings: V2Settings());

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Single(capture.SeenActiveToolNames);
        Assert.DoesNotContain("set_view_crop", capture.SeenActiveToolNames[0]);
        Assert.Contains(llm.Requests[1].Tools, tool => tool.Name == "set_view_crop");
    }

    [Fact]
    public async Task RunTurn_ConversationChanged_ResetsLoadedAndPendingTools()
    {
        var store = new InMemoryConversationStore();
        var tools = CoreTools();
        var registry = new AgentToolRegistry(tools);
        var previousConversation = Guid.NewGuid();
        var session = new AgentSessionContext { ConversationId = previousConversation };
        session.LoadedToolNames.Add("set_view_crop");
        session.PendingToolNames.Add("apply_view_template");
        var executor = CreateExecutor(registry, session);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("ok");
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, session, settings: V2Settings());

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Empty(session.LoadedToolNames);
        Assert.Empty(session.PendingToolNames);
    }

    private static AgentToolExecutor CreateExecutor(
        IToolRegistry registry,
        AgentSessionContext session) =>
        new(registry, new ImmediateAwaitableEvent(), session, new AutoApproveConfirmation());

    private sealed class ActiveToolsCapturingTool : IAgentTool
    {
        public ActiveToolsCapturingTool(string name)
        {
            Definition = new AgentToolDefinition
            {
                Name = name,
                Description = name + " capturing tool",
                Risk = AgentToolRisk.Read,
                InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}"
            };
        }

        public AgentToolDefinition Definition { get; }

        public List<IReadOnlyCollection<string>> SeenActiveToolNames { get; } = new();

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct)
        {
            SeenActiveToolNames.Add(context.ActiveToolNames.ToList());
            return Task.FromResult(ToolResult.Ok(null, "captured"));
        }
    }
}
