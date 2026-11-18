using Xunit;
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
    public async Task RunTurn_ContextCompactionV2Off_UsesMessageOnlyEstimate()
    {
        var toolMessages = await RunTurnWithCompactionFlag(enabled: false);

        Assert.Equal(5, toolMessages.Count);
        Assert.All(toolMessages, message =>
            Assert.Contains("original-result-", message.Content, StringComparison.Ordinal));
        Assert.DoesNotContain(toolMessages, message =>
            (message.Content ?? string.Empty).Contains("\"compacted\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTurn_ContextCompactionV2On_EstimatesSystemAndToolsToo()
    {
        var toolMessages = await RunTurnWithCompactionFlag(enabled: true);

        Assert.Equal(5, toolMessages.Count);
        Assert.Contains(toolMessages, message =>
            (message.Content ?? string.Empty).Contains("\"compacted\":true", StringComparison.Ordinal));
        Assert.Contains(toolMessages, message =>
            (message.Content ?? string.Empty).Contains("original-result-", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<AgentMessage>> RunTurnWithCompactionFlag(bool enabled)
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
            ContextThreshold = 0.7,
            ContextCompactionV2 = enabled
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
}
