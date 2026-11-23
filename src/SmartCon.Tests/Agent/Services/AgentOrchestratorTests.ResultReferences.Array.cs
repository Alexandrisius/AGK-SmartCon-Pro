using System.Text.Json;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// A reference written inside an array argument, and the text of a tool
/// result as the model reads it.
/// </summary>
public sealed partial class AgentOrchestratorTests
{
    // The reference object with its braces: the form a model writes inside an array.
    private const string ReferenceObject = "{" + Reference + "}";

    private static async Task<(JsonElement? Received, FakeAgentLlmClient Llm)> RunConsumerAsync(
        string consumerArguments,
        string sourceTool = "get_source",
        string sourceArguments = "{}",
        string sourceCallId = "call_big")
    {
        JsonElement? received = null;
        var source = BigSourceTool();
        if (sourceTool != "get_source")
        {
            source = new ScriptedTool(
                sourceTool,
                AgentToolRisk.Read,
                supportsDryRun: false,
                schema: @"{""type"":""object"",""properties"":{}}",
                handler: _ => ToolResult.Ok(
                    JsonSerializer.SerializeToElement(new
                    {
                        family = "Кран шаровой",
                        breadcrumb = "Инженерные системы › Трубопроводная арматура › Краны шаровые"
                    }),
                    "Предложен код для «Кран шаровой»"));
        }

        var consumer = ConsumerTool(args => received = args.Clone());
        var registry = new AgentToolRegistry(new IAgentTool[] { source, consumer });
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = sourceCallId, Name = sourceTool, ArgumentsJson = sourceArguments } });
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = "call_use", Name = "use_source", ArgumentsJson = consumerArguments } });
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(
            llm,
            CreateExecutor(registry),
            registry,
            new InMemoryConversationStore(),
            settings: new AgentLlmSettings { Model = "test-model" },
            toolResultBlobStore: new MemoryBlobStore());

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "заполни",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        return (received, llm);
    }

    [Fact]
    public async Task RunTurn_ReferenceAsTheOnlyArrayElement_BecomesThatArray()
    {
        // The natural way to say "items = that stored list": the reference is
        // wrapped in brackets. Nested one level deeper it failed the schema.
        var (received, llm) = await RunConsumerAsync("{\"items\":[" + ReferenceObject + "]}");

        var used = ToolResultOf(llm, 2);
        Assert.True(used.Contains("\"success\":true", StringComparison.Ordinal), used);
        var items = Assert.IsType<JsonElement>(received).GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(3000, items.GetArrayLength());
        Assert.Equal(JsonValueKind.Object, items[0].ValueKind);
        Assert.Equal(1, items[0].GetProperty("elementId").GetInt32());
    }

    [Fact]
    public async Task RunTurn_ReferenceInsideAnArray_IsSplicedBetweenTheOtherElements()
    {
        var (received, llm) = await RunConsumerAsync(
            "{\"items\":[{\"elementId\":7,\"value\":\"first\"}," + ReferenceObject +
            ",{\"elementId\":9,\"value\":\"last\"}]}");

        Assert.Contains("\"success\":true", ToolResultOf(llm, 2), StringComparison.Ordinal);
        var items = Assert.IsType<JsonElement>(received).GetProperty("items");
        Assert.Equal(3002, items.GetArrayLength());
        Assert.Equal(7, items[0].GetProperty("elementId").GetInt32());
        Assert.Equal(1, items[1].GetProperty("elementId").GetInt32());
        Assert.Equal(3000, items[3000].GetProperty("elementId").GetInt32());
        Assert.Equal(9, items[3001].GetProperty("elementId").GetInt32());
    }

    [Fact]
    public async Task RunTurn_ReferenceInsideAnArray_UnknownHandle_IsAResultNotFoundFailure()
    {
        var (received, llm) = await RunConsumerAsync(
            "{\"items\":[" + ReferenceObject.Replace("call_big", "call_missing") + "]}");

        Assert.Null(received);
        var used = ToolResultOf(llm, 2);
        Assert.Contains("result_not_found", used, StringComparison.Ordinal);
        Assert.Contains("An argument reference cannot be resolved", used, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_ToolResult_KeepsNonAsciiTextForTheModel()
    {
        // The sanitized JSON is what the model reads: with the default encoder
        // every Cyrillic letter became \uXXXX - six characters, and misread.
        var (_, llm) = await RunConsumerAsync(
            "{\"items\":[]}",
            sourceTool: "get_names",
            sourceCallId: "call_names");

        var result = ToolResultOf(llm, 1);
        Assert.Contains("Кран шаровой", result, StringComparison.Ordinal);
        Assert.Contains("Краны шаровые", result, StringComparison.Ordinal);
        Assert.Contains("Предложен код для «Кран шаровой»", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u04", result, StringComparison.Ordinal);
    }
}
