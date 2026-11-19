using Xunit;
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
    public async Task RunTurn_EnabledDynamicRanker_SuppliesTheTurnStartToolSet()
    {
        var tools = Enumerable.Range(0, 9)
            .Select(index => (IAgentTool)new CountingTool(name: "tool_" + index))
            .ToList();
        var registry = new AgentToolRegistry(tools);
        var ranker = new FakeDynamicRanker("tool_8");

        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("Готово");

        var orchestrator = CreateOrchestrator(
            llm,
            CreateExecutor(registry),
            registry,
            new InMemoryConversationStore(),
            dynamicRanker: ranker);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "переименуй виды",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal(1, ranker.Calls);
        Assert.Single(llm.Requests[0].Tools);
        Assert.Equal("tool_8", llm.Requests[0].Tools[0].Name);
    }

    [Fact]
    public async Task RunTurn_DisabledDynamicRanker_KeepsBm25Selection()
    {
        var tools = Enumerable.Range(0, 9)
            .Select(index => (IAgentTool)new CountingTool(name: "tool_" + index))
            .ToList();
        var registry = new AgentToolRegistry(tools);
        var ranker = new FakeDynamicRanker("tool_8", enabled: false);

        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("Готово");

        var orchestrator = CreateOrchestrator(
            llm,
            CreateExecutor(registry),
            registry,
            new InMemoryConversationStore(),
            dynamicRanker: ranker);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "переименуй виды",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal(0, ranker.Calls);
        Assert.DoesNotContain(llm.Requests[0].Tools, tool => tool.Name == "tool_8");
    }

    private sealed class FakeDynamicRanker : IDynamicToolRanker
    {
        private readonly string _toolName;

        public FakeDynamicRanker(string toolName, bool enabled = true)
        {
            _toolName = toolName;
            IsEnabled = enabled;
        }

        public bool IsEnabled { get; }

        public int Calls { get; private set; }

        public Task<IReadOnlyList<AgentToolDefinition>> RankAsync(
            string query,
            IReadOnlyList<AgentToolDefinition> tools,
            int max,
            CancellationToken ct = default)
        {
            Calls++;
            var match = tools.FirstOrDefault(tool =>
                string.Equals(tool.Name, _toolName, StringComparison.Ordinal));
            return Task.FromResult<IReadOnlyList<AgentToolDefinition>>(
                match is null ? Array.Empty<AgentToolDefinition>() : new[] { match });
        }
    }
}