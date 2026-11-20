using System.Text.Json;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// P.4: the tool set belongs to the conversation. It is chosen once, sent in
/// canonical name order and only extended by load_tool_pack, so the tool block
/// stays byte-identical between turns and the provider cache survives.
/// </summary>
public sealed class AgentFrozenToolSetTests
{
    [Fact]
    public async Task ToolSetHash_StaysIdenticalAcrossFiveTurnsWithDifferentUserText()
    {
        var registry = BuildRegistry();
        var store = new InMemoryConversationStore();
        var llm = new FakeAgentLlmClient();
        var orchestrator = AgentHarness.Create(llm, registry, store);
        var conversationId = Guid.NewGuid();
        var texts = new[]
        {
            "переименуй виды",
            "покажи листы проекта",
            "какие есть уровни",
            "проверь параметры элементов",
            "продолжай"
        };

        foreach (var text in texts)
        {
            llm.EnqueueAssistant("ответ");
            await orchestrator.RunTurnAsync(
                conversationId,
                text,
                Array.Empty<AgentAttachment>(),
                null,
                CancellationToken.None);
        }

        var firstHash = AgentPromptHash.ComputeToolSet(llm.Requests[0].Tools);
        var firstNameList = llm.Requests[0].Tools.Select(tool => tool.Name).ToList();
        Assert.Equal(firstNameList.OrderBy(name => name, StringComparer.Ordinal), firstNameList);

        for (var turn = 1; turn < llm.Requests.Count; turn++)
        {
            Assert.Equal(firstHash, AgentPromptHash.ComputeToolSet(llm.Requests[turn].Tools));
            Assert.Equal(
                firstNameList,
                llm.Requests[turn].Tools.Select(tool => tool.Name).ToList());
        }
    }

    [Fact]
    public async Task LaterUserText_DoesNotAddToolsWithoutLoadToolPack()
    {
        var registry = BuildRegistry();
        var store = new InMemoryConversationStore();
        var llm = new FakeAgentLlmClient();
        var orchestrator = AgentHarness.Create(llm, registry, store);
        var conversationId = Guid.NewGuid();

        llm.EnqueueAssistant("ответ");
        await orchestrator.RunTurnAsync(
            conversationId,
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.DoesNotContain(llm.Requests[0].Tools, tool => tool.Name == "list_sheets");
        Assert.DoesNotContain(llm.Requests[0].Tools, tool => tool.Name == "list_schedules");

        llm.EnqueueAssistant("ответ");
        await orchestrator.RunTurnAsync(
            conversationId,
            "проверь все листы и спецификации",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.DoesNotContain(llm.Requests[1].Tools, tool => tool.Name == "list_sheets");
        Assert.DoesNotContain(llm.Requests[1].Tools, tool => tool.Name == "list_schedules");
    }

    [Fact]
    public async Task FirstUserText_SelectsTheInitialShortlistOnce()
    {
        var registry = BuildRegistry();
        var store = new InMemoryConversationStore();
        var llm = new FakeAgentLlmClient();
        var orchestrator = AgentHarness.Create(llm, registry, store);

        llm.EnqueueAssistant("ответ");
        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "проверь все листы и спецификации",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var sent = llm.Requests[0].Tools;
        Assert.Contains(sent, tool => tool.Name == "list_sheets");
        Assert.Contains(sent, tool => tool.Name == "list_schedules");
        foreach (var core in AgentToolPacks.CoreToolNames)
            Assert.Contains(sent, tool => tool.Name == core);
    }

    [Fact]
    public async Task LoadToolPack_ExtendsTheFrozenSetAtTheRoundBoundary()
    {
        var session = new AgentSessionContext();
        var loadTool = new LoadToolPackTool(session);
        var tools = CoreTools();
        tools.Add(new ScriptedTool("get_view_settings", AgentToolRisk.Read));
        tools.Add(new ScriptedTool("set_view_crop", AgentToolRisk.Write));
        tools.Add(new ScriptedTool("set_view_range", AgentToolRisk.Write));
        tools.Add(loadTool);
        var registry = new AgentToolRegistry(tools, null, loadTool);
        var store = new InMemoryConversationStore();
        var llm = new FakeAgentLlmClient();
        var orchestrator = AgentHarness.Create(llm, registry, store, session: session);

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
        llm.EnqueueAssistant("ответ");

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var before = llm.Requests[0].Tools;
        var after = llm.Requests[1].Tools;
        Assert.DoesNotContain(before, tool => tool.Name == "set_view_crop");
        Assert.Contains(after, tool => tool.Name == "set_view_crop");
        Assert.NotEqual(
            AgentPromptHash.ComputeToolSet(before),
            AgentPromptHash.ComputeToolSet(after));
        Assert.Equal(
            after.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal),
            after.Select(tool => tool.Name));
        Assert.Contains("set_view_crop", session.FrozenToolNames);
    }

    [Fact]
    public async Task ConversationChanged_ClearsTheFrozenSet()
    {
        var registry = BuildRegistry();
        var store = new InMemoryConversationStore();
        var llm = new FakeAgentLlmClient();
        var session = new AgentSessionContext { ConversationId = Guid.NewGuid() };
        session.FreezeTool("list_sheets");
        var orchestrator = AgentHarness.Create(llm, registry, store, session: session);

        llm.EnqueueAssistant("ответ");
        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.DoesNotContain("list_sheets", session.FrozenToolNames);
        Assert.Empty(session.LoadedToolNames);
        Assert.Empty(session.PendingToolNames);
    }

    [Fact]
    public void ToolSetHash_IgnoresSchemaPropertyOrder()
    {
        var first = Tool(
            "rename_views",
            """{"type":"object","properties":{"items":{"type":"array"},"dryRun":{"type":"boolean"}}}""");
        var reordered = Tool(
            "rename_views",
            """{"properties":{"dryRun":{"type":"boolean"},"items":{"type":"array"}},"type":"object"}""");

        Assert.Equal(
            AgentPromptHash.ComputeToolSet(new[] { first }),
            AgentPromptHash.ComputeToolSet(new[] { reordered }));
    }

    [Fact]
    public void Payload_IsByteIdenticalForTheSameToolSet()
    {
        var tools = new[]
        {
            Tool("zeta_tool", """{"type":"object","properties":{"b":{"type":"string"},"a":{}}}"""),
            Tool("alpha_tool", """{"type":"object","properties":{}}""")
        };
        var request = new AgentChatRequest
        {
            Model = "test-model",
            Messages = new[] { AgentMessage.User("привет") },
            Tools = tools
        };
        var reversed = new AgentChatRequest
        {
            Model = "test-model",
            Messages = new[] { AgentMessage.User("привет") },
            Tools = new[] { tools[1], tools[0] }
        };

        var first = JsonSerializer.Serialize(LlmPayloadBuilder.Build(request));
        var second = JsonSerializer.Serialize(LlmPayloadBuilder.Build(reversed));

        Assert.Equal(first, second);
        Assert.Contains("\"alpha_tool\"", first, StringComparison.Ordinal);
        Assert.True(
            first.IndexOf("alpha_tool", StringComparison.Ordinal)
            < first.IndexOf("zeta_tool", StringComparison.Ordinal),
            "tools must be serialized in name order");
    }

    private static AgentToolRegistry BuildRegistry()
    {
        var tools = CoreTools();
        for (var index = 0; index < 20; index++)
            tools.Add(new ScriptedTool("filler_" + index, AgentToolRisk.Read));

        tools.Add(new ScriptedTool("list_sheets", AgentToolRisk.Read));
        tools.Add(new ScriptedTool("list_schedules", AgentToolRisk.Read));
        tools.Add(new ScriptedTool("add_family_parameter", AgentToolRisk.Write));
        return new AgentToolRegistry(tools);
    }

    private static List<IAgentTool> CoreTools() =>
        AgentToolPacks.CoreToolNames
            .Select(name => (IAgentTool)new ScriptedTool(name, AgentToolRisk.Read))
            .ToList();

    private static AgentToolDefinition Tool(string name, string schema) =>
        new()
        {
            Name = name,
            Description = name,
            InputSchemaJson = schema
        };
}