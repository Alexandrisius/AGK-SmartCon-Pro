using System.Text.Json;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Harness reactions to call storms (C7-06, C2-06, C9-07) and to a host that
/// stopped answering (C1-01), the tool-name echo (DB-01) and the plans skill on
/// the first execute_plan result (C4-15). Every reaction is a warning on a tool
/// result: none is a new round, time or step limit, and none ends the turn.
/// </summary>
public sealed partial class AgentOrchestratorTests
{
    private const string SameErrorText = "already failed in round(s) 1, 2 with the same error";
    private const string SameResultText = "already returned this result in round(s) 1, 2";
    private const string SameResultAction = "change the arguments or choose another path";
    private const string BatchText = "this tool takes several items in one call; batch them";

    private static AgentToolCall CallOf(
        string id,
        string argumentsJson,
        string name = "get_test") => new()
    {
        Id = id,
        Name = name,
        ArgumentsJson = argumentsJson
    };

    /// <summary>Payloads the model saw in the given request (4 requests = 3 tool rounds + answer).</summary>
    private static List<string> ToolPayloads(FakeAgentLlmClient llm, int requestIndex) =>
        llm.Requests[requestIndex].Messages
            .Where(message => message.Role == AgentRole.Tool)
            .Select(message => message.Content ?? string.Empty)
            .ToList();

    private static ScriptedTool ReadTool(Func<JsonElement, ToolResult> handler) =>
        new("get_test", AgentToolRisk.Read, supportsDryRun: false, handler: handler);

    [Fact]
    public async Task RunTurn_SameToolSameErrorThreeTimes_WarnsWithTheEarlierRounds()
    {
        var tool = ReadTool(_ => ToolResult.Fail("file_missing", "The file is not configured."));
        var llm = new FakeAgentLlmClient();
        foreach (var json in new[] { "{\"viewId\":1}", "{\"viewId\":2}", "{\"viewId\":3}" })
            llm.EnqueueAssistant(null, new[] { CallOf("call_" + json, json) });
        llm.EnqueueAssistant("не вышло");

        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var orchestrator = CreateOrchestrator(
            llm, CreateExecutor(registry), registry, new InMemoryConversationStore());
        await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "нет файла", Array.Empty<AgentAttachment>(), null, CancellationToken.None);

        var payloads = ToolPayloads(llm, 3);
        Assert.Equal(3, payloads.Count);
        Assert.DoesNotContain(SameErrorText, payloads[0], StringComparison.Ordinal);
        Assert.DoesNotContain(SameErrorText, payloads[1], StringComparison.Ordinal);
        Assert.Contains(SameErrorText, payloads[2], StringComparison.Ordinal);
        Assert.Contains("(file_missing)", payloads[2], StringComparison.Ordinal);
        Assert.Contains("choose another path", payloads[2], StringComparison.Ordinal);
        // A hint only: all three calls ran and the turn went on to the model's answer.
        Assert.Equal(4, llm.Requests.Count);
    }

    [Fact]
    public async Task RunTurn_IdenticalReadCallWithTheSameResultThirdTime_Warns()
    {
        var tool = ReadTool(_ => ToolResult.Ok(null, "same answer"));
        var llm = new FakeAgentLlmClient();
        foreach (var index in new[] { 1, 2, 3 })
            llm.EnqueueAssistant(null, new[] { CallOf("call_" + index, "{}") });
        llm.EnqueueAssistant("done");

        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var orchestrator = CreateOrchestrator(
            llm, CreateExecutor(registry), registry, new InMemoryConversationStore());
        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "перечитай", Array.Empty<AgentAttachment>(), null, CancellationToken.None);

        var payloads = ToolPayloads(llm, 3);
        Assert.Equal(3, payloads.Count);
        Assert.DoesNotContain(SameResultText, payloads[1], StringComparison.Ordinal);
        Assert.Contains(SameResultText, payloads[2], StringComparison.Ordinal);
        Assert.Contains(SameResultAction, payloads[2], StringComparison.Ordinal);
        Assert.Equal("done", result.FinalText);
    }

    [Fact]
    public async Task RunTurn_SixIdenticalSuccessfulReadCalls_WarnFromTheThirdOn()
    {
        // ORCH-12: a read tool called six times with the very same arguments and
        // the same successful answer, while the model believed it was passing a
        // filter. The guard exempts read tools, so the hint is the only signal.
        const string arguments = "{\"groupBy\":\"family\",\"includeZeroCount\":false,\"topN\":50}";
        var tool = new ScriptedTool(
            "summarize_elements",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new { groups = new[] { new { name = "a", count = 3 } } }),
                "Summarized 3 element(s)."),
            schema: "{\"type\":\"object\",\"properties\":{\"groupBy\":{\"type\":\"string\"}," +
                    "\"includeZeroCount\":{\"type\":\"boolean\"},\"topN\":{\"type\":\"integer\"}}}");
        var llm = new FakeAgentLlmClient();
        for (var round = 1; round <= 6; round++)
        {
            llm.EnqueueAssistant(
                null,
                new[] { CallOf("call_" + round, arguments, "summarize_elements") });
        }

        llm.EnqueueAssistant("done");
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var orchestrator = CreateOrchestrator(
            llm, CreateExecutor(registry), registry, new InMemoryConversationStore());
        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "сводка", Array.Empty<AgentAttachment>(), null, CancellationToken.None);

        // No new limit: all six calls ran and the model got its seventh request.
        Assert.Equal(6, result.ToolCallCount);
        var payloads = ToolPayloads(llm, 6);
        Assert.Equal(6, payloads.Count);
        Assert.DoesNotContain(payloads.Take(2), payload => payload.Contains(SameResultText, StringComparison.Ordinal));
        for (var index = 2; index < payloads.Count; index++)
        {
            // Result of call index+1: the hint names every earlier round.
            var rounds = string.Join(", ", Enumerable.Range(1, index));
            Assert.Contains("already returned this result in round(s) " + rounds, payloads[index], StringComparison.Ordinal);
            Assert.Contains(SameResultAction, payloads[index], StringComparison.Ordinal);
        }

        Assert.Contains("the change did not reach the call", payloads[5], StringComparison.Ordinal);
        Assert.Equal("done", result.FinalText);
    }

    [Fact]
    public async Task RunTurn_ThreeSingleItemCallsOfABatchTool_SuggestsBatching()
    {
        var tool = new ScriptedTool("rename_views", AgentToolRisk.Write);
        var llm = new FakeAgentLlmClient();
        foreach (var index in new[] { 1, 2, 3 })
            llm.EnqueueAssistant(
                null,
                new[]
                {
                    new AgentToolCall
                    {
                        Id = "call_" + index,
                        Name = "rename_views",
                        ArgumentsJson = "{\"items\":[{\"viewId\":" + index + "}]}"
                    }
                });
        llm.EnqueueAssistant("done");

        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var orchestrator = CreateOrchestrator(
            llm, CreateExecutor(registry), registry, new InMemoryConversationStore());
        await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "переименуй", Array.Empty<AgentAttachment>(), null, CancellationToken.None);

        var payloads = ToolPayloads(llm, 3);
        Assert.Equal(3, payloads.Count);
        Assert.DoesNotContain(BatchText, payloads[0], StringComparison.Ordinal);
        Assert.DoesNotContain(BatchText, payloads[1], StringComparison.Ordinal);
        Assert.Contains(BatchText, payloads[2], StringComparison.Ordinal);
        // No quotes in the hint: the default JSON encoder would turn each one into \u0027.
        Assert.Contains("calls of rename_views in round(s)", payloads[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_TimeoutsStillRunningInARow_WarnsAndNeverEndsTheTurn()
    {
        // C1-01 named the storm: three calls into a host that is not answering
        // burned 210 of 262 seconds. The harness names it on the result from the
        // second call on; it does not end the turn — only Stop and the loop
        // guard do — so a slow host that recovers still gets its answer.
        var stuck = ToolResult.Fail(
            "tool_timeout",
            "still running",
            data: JsonSerializer.SerializeToElement(new { stillRunning = true }));
        var tool = ReadTool(_ => stuck);
        var llm = new FakeAgentLlmClient();
        foreach (var index in new[] { 1, 2, 3 })
            llm.EnqueueAssistant(null, new[] { CallOf("call_" + index, "{}") });
        llm.EnqueueAssistant("хост молчит, сообщаю пользователю");
        var usageStore = new CapturingUsageStore();

        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var orchestrator = CreateOrchestrator(
            llm, CreateExecutor(registry), registry, new InMemoryConversationStore(),
            usageStore: usageStore);
        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "долго", Array.Empty<AgentAttachment>(), null, CancellationToken.None);

        var payloads = ToolPayloads(llm, 3);
        Assert.Equal(3, payloads.Count);
        Assert.DoesNotContain("timed out while still running", payloads[0], StringComparison.Ordinal);
        Assert.Contains("2 tool calls in a row timed out while still running", payloads[1], StringComparison.Ordinal);
        Assert.Contains("host is not answering", payloads[2], StringComparison.Ordinal);
        Assert.Contains("tell the user what is blocked", payloads[2], StringComparison.Ordinal);

        // All three calls ran, the model got its fourth request and answered.
        Assert.Equal(4, llm.Requests.Count);
        Assert.Equal(3, result.ToolCallCount);
        Assert.Equal("хост молчит, сообщаю пользователю", result.FinalText);
        Assert.Equal("ok", Assert.Single(usageStore.Records).Status);
    }

    [Fact]
    public async Task RunTurn_TimeoutThenAnswer_TheNextTimeoutStartsANewStreak()
    {
        var stuck = ToolResult.Fail(
            "tool_timeout",
            "still running",
            data: JsonSerializer.SerializeToElement(new { stillRunning = true }));
        var answers = new Queue<ToolResult>(
            new[] { stuck, ToolResult.Ok(null, "answered"), stuck, ToolResult.Ok(null, "answered") });
        var tool = ReadTool(_ => answers.Dequeue());
        var llm = new FakeAgentLlmClient();
        foreach (var index in new[] { 1, 2, 3, 4 })
            llm.EnqueueAssistant(null, new[] { CallOf("call_" + index, "{\"viewId\":" + index + "}") });
        llm.EnqueueAssistant("done");

        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var orchestrator = CreateOrchestrator(
            llm, CreateExecutor(registry), registry, new InMemoryConversationStore());
        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "с перерывами", Array.Empty<AgentAttachment>(), null, CancellationToken.None);

        Assert.Equal("done", result.FinalText);
        Assert.Equal(5, llm.Requests.Count);
        Assert.DoesNotContain(
            ToolPayloads(llm, 4), payload => payload.Contains("in a row timed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTurn_ToolPayloadNamesItsTool_WireMessageStaysProtocolClean()
    {
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(null, new[] { CallOf("call_1", "{}") });
        llm.EnqueueAssistant("done");
        var registry = new AgentToolRegistry(new IAgentTool[] { new CountingTool() });
        var orchestrator = CreateOrchestrator(
            llm, CreateExecutor(registry), registry, new InMemoryConversationStore());
        await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "привет", Array.Empty<AgentAttachment>(), null, CancellationToken.None);

        var payload = ToolPayloads(llm, 1).Single();
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("get_test", document.RootElement.GetProperty("tool").GetString());

        // The echo lives in the content string only: the protocol message keeps
        // exactly the three OpenAI-compatible tool-message fields.
        var wire = LlmPayloadBuilder.Build(llm.Requests[1]);
        var toolMessage = ((List<object?>)wire["messages"]!)
            .Cast<Dictionary<string, object?>>()
            .Single(message => string.Equals(message["role"] as string, "tool", StringComparison.Ordinal));
        Assert.Equal(
            new[] { "content", "role", "tool_call_id" },
            toolMessage.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.Equal("tool", toolMessage["role"]);
        Assert.Equal("call_1", toolMessage["tool_call_id"]);
    }

    [Fact]
    public async Task RunTurn_BlockedCall_PayloadNamesItsToolToo()
    {
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(null, new[] { CallOf("call_1", "{}"), CallOf("call_2", "{}") });
        llm.EnqueueAssistant(null, new[] { CallOf("call_3", "{}") });
        llm.EnqueueAssistant("done");
        var registry = new AgentToolRegistry(
            new IAgentTool[] { new CountingTool(risk: AgentToolRisk.Write) });
        var orchestrator = CreateOrchestrator(
            llm, CreateExecutor(registry), registry, new InMemoryConversationStore());
        await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "цикл", Array.Empty<AgentAttachment>(), null, CancellationToken.None);

        // The third request carries the results of all three calls; the blocked one is last.
        var blocked = ToolPayloads(llm, 2).Last();
        Assert.Contains("loop_detected", blocked, StringComparison.Ordinal);
        Assert.Contains("\"tool\":\"get_test\"", blocked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_FirstExecutePlanResult_CarriesThePlansSkill()
    {
        // execute_plan is always callable, so the model reaches it before any
        // load_tool_pack: the manual must travel with the FIRST result, a
        // successful dry run included, not only with a validation error (C4-15).
        var tool = new ScriptedTool("execute_plan", AgentToolRisk.Write);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null, new[] { CallOf("call_1", "{\"items\":[{\"viewId\":1}]}", "execute_plan") });
        llm.EnqueueAssistant(
            null,
            new[] { CallOf("call_2", "{\"items\":[{\"viewId\":1}],\"dryRun\":false}", "execute_plan") });
        llm.EnqueueAssistant("готово");

        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var session = new AgentSessionContext();
        var orchestrator = CreateOrchestrator(
            llm, CreateExecutor(registry), registry, new InMemoryConversationStore(), session);
        await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "план", Array.Empty<AgentAttachment>(), null, CancellationToken.None);

        var payloads = ToolPayloads(llm, 2);
        Assert.Equal(2, payloads.Count);
        using var first = JsonDocument.Parse(payloads[0]);
        Assert.True(first.RootElement.GetProperty("success").GetBoolean());
        var skill = Assert.Single(first.RootElement.GetProperty("skills").EnumerateArray());
        Assert.Equal("plans", skill.GetProperty("pack").GetString());
        Assert.Contains("execute_plan", skill.GetProperty("markdown").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"skills\"", payloads[1], StringComparison.Ordinal);
        Assert.Contains("plans", session.SentPackSkills);
    }
}
