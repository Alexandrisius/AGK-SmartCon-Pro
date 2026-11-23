using System.Text.Json;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// A tool argument may point at a part of a stored tool result
/// ({"$result": handle, "path": …}) instead of the model paging the stored
/// result into the context and retyping it.
/// </summary>
public sealed partial class AgentOrchestratorTests
{
    private const string Reference = "\"$result\":\"call_big\",\"path\":\"data/setParameters/items\"";

    private static ScriptedTool BigSourceTool() => new(
        "get_source",
        AgentToolRisk.Read,
        supportsDryRun: false,
        schema: @"{""type"":""object"",""properties"":{}}",
        handler: _ => ToolResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                summary = new { proposed = 3000 },
                setParameters = new
                {
                    items = Enumerable.Range(1, 3000)
                        .Select(index => new { elementId = index, value = "10.10.10" })
                        .ToArray()
                }
            }),
            "ok"));

    private static ScriptedTool ConsumerTool(Action<JsonElement>? onExecute = null) => new(
        "use_source",
        AgentToolRisk.Read,
        supportsDryRun: false,
        handler: args =>
        {
            onExecute?.Invoke(args);
            return ToolResult.Ok(JsonSerializer.SerializeToElement(new { done = true }), "done");
        });

    private static async Task<(ScriptedTool Consumer, FakeAgentLlmClient Llm)> RunWithReferenceAsync(
        string consumerArguments,
        IAgentToolResultBlobStore blobStore)
    {
        var source = BigSourceTool();
        var consumer = ConsumerTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { source, consumer });
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = "call_big", Name = "get_source", ArgumentsJson = "{}" } });
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
            toolResultBlobStore: blobStore);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "заполни",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        return (consumer, llm);
    }

    private static string ToolResultOf(FakeAgentLlmClient llm, int request) =>
        llm.Requests[request].Messages.Last(message => message.Role == AgentRole.Tool).Content ?? string.Empty;

    [Fact]
    public async Task RunTurn_ArgumentReference_IsReplacedByTheStoredValue()
    {
        JsonElement? received = null;
        var source = BigSourceTool();
        var consumer = ConsumerTool(args => received = args.Clone());
        var registry = new AgentToolRegistry(new IAgentTool[] { source, consumer });
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = "call_big", Name = "get_source", ArgumentsJson = "{}" } });
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = "call_use", Name = "use_source", ArgumentsJson = "{\"items\":{" + Reference + "}}" } });
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

        // The stored result was trimmed (so it IS stored) and the model never read it.
        Assert.Contains("\"resultHandle\":\"call_big\"", ToolResultOf(llm, 1), StringComparison.Ordinal);
        var used = ToolResultOf(llm, 2);
        Assert.Contains("\"success\":true", used, StringComparison.Ordinal);
        Assert.DoesNotContain("read_tool_result", used, StringComparison.Ordinal);

        var items = Assert.IsType<JsonElement>(received).GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(3000, items.GetArrayLength());
        Assert.Equal(1, items[0].GetProperty("elementId").GetInt32());
        Assert.Equal("10.10.10", items[2999].GetProperty("value").GetString());
    }

    [Fact]
    public async Task RunTurn_ArgumentReference_UnknownHandle_IsAResultNotFoundFailure()
    {
        var (consumer, llm) = await RunWithReferenceAsync(
            "{\"items\":{" + Reference.Replace("call_big", "call_missing") + "}}",
            new MemoryBlobStore());

        Assert.Empty(consumer.Executions);
        var used = ToolResultOf(llm, 2);
        Assert.Contains("result_not_found", used, StringComparison.Ordinal);
        Assert.Contains("An argument reference cannot be resolved", used, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_ArgumentReference_UnknownPath_IsAnInvalidArgumentsFailure()
    {
        var (consumer, llm) = await RunWithReferenceAsync(
            "{\"items\":{" + Reference.Replace("data/setParameters/items", "data/nothing") + "}}",
            new MemoryBlobStore());

        Assert.Empty(consumer.Executions);
        Assert.Contains("invalid_arguments", ToolResultOf(llm, 2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_ObjectWithResultKeyButThreeProperties_IsPassedUntouched()
    {
        JsonElement? received = null;
        var source = BigSourceTool();
        var consumer = ConsumerTool(args => received = args.Clone());
        var registry = new AgentToolRegistry(new IAgentTool[] { source, consumer });
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = "call_big", Name = "get_source", ArgumentsJson = "{}" } });
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "call_use",
                    Name = "use_source",
                    ArgumentsJson = "{\"viewId\":7,\"items\":[{\"$result\":\"call_big\",\"path\":\"x\",\"extra\":1}]}"
                }
            });
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

        // Not a reference (three properties): the tool got the object as written.
        var item = Assert.IsType<JsonElement>(received).GetProperty("items")[0];
        Assert.Equal(3, item.EnumerateObject().Count());
        Assert.Equal(1, item.GetProperty("extra").GetInt32());
    }

    private sealed class MemoryBlobStore : IAgentToolResultBlobStore
    {
        private readonly Dictionary<string, string> _blobs = new(StringComparer.Ordinal);

        public Task SaveAsync(Guid sessionId, string toolCallId, string resultJson, CancellationToken ct)
        {
            _blobs[toolCallId] = resultJson;
            return Task.CompletedTask;
        }

        public Task<AgentToolResultBlob?> GetAsync(Guid sessionId, string toolCallId, CancellationToken ct) =>
            Task.FromResult<AgentToolResultBlob?>(_blobs.TryGetValue(toolCallId, out var json)
                ? new AgentToolResultBlob
                {
                    ToolCallId = toolCallId,
                    SessionId = sessionId,
                    ResultJson = json
                }
                : null);
    }
}
