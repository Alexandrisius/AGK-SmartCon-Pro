using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Apply message and hint of execute_plan around the read-back (review r1,
/// finding 1). A read-back that was requested but could not run is a
/// mismatch, never "verification not requested": the applied changes are
/// unverified and the message has to say so.
/// </summary>
public sealed class ExecutePlanReadBackReportTests
{
    private const string WriteSchema =
        @"{""type"":""object"",""properties"":{""items"":{""type"":""array""},""dryRun"":{""type"":""boolean""}}," +
        @"""additionalProperties"":false}";

    private const string ReadSchema =
        @"{""type"":""object"",""properties"":{},""additionalProperties"":false}";

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ExecutePlanTool CreateTool(params IAgentTool[] tools) =>
        new(() => new FakeToolRegistry(tools), new ImmediateAwaitableEvent(),
            new RecordingAuditLog());

    private static Task<ToolResult> ApplyAsync(ExecutePlanTool tool, string plan) =>
        tool.ExecuteAsync(
            new AgentToolContext(new ImmediateAwaitableEvent(), "doc", Guid.Empty)
            {
                WriteApproved = true
            },
            Parse(plan),
            CancellationToken.None);

    private static ScriptedTool ScheduleWriter() =>
        new(
            "create_schedule",
            handler: _ => ToolResult.Ok(
                Parse("""
                    {"dryRun":false,"changed":[{"id":1}],"skipped":[],"failed":[],"total":1,"affected":1,
                     "created":{"scheduleId":1733617,"name":"Report"}}
                    """),
                "applied", 1),
            schema: WriteSchema);

    private static ScriptedTool FailingReader() =>
        new(
            "get_schedule_definition",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Fail("schedule_not_found", "Schedule 1733593 not found"),
            schema: ReadSchema);

    private const string PlanWithReadBack =
        "{\"goal\":\"verify\",\"dryRun\":false,\"steps\":[{" +
        "\"stepId\":\"1\",\"toolName\":\"create_schedule\",\"arguments\":{\"items\":[1]}," +
        "\"readBackTool\":\"READER\",\"readBackArguments\":{}," +
        "\"readBackExpectations\":[{\"path\":\"sortGroupFieldCount\",\"expected\":\"2\"}]}]}";

    private const string PlanWithoutReadBack =
        "{\"goal\":\"verify\",\"dryRun\":false,\"steps\":[" +
        "{\"stepId\":\"1\",\"toolName\":\"create_schedule\",\"arguments\":{\"items\":[1]}}]}";

    private const string ToolReadBackBlock =
        "\"readBack\":{\"expected\":2,\"verified\":2,\"mismatched\":0,\"notVerifiable\":0}";

    // A write tool whose dry run passes; the apply either commits or is rolled
    // back — and the rolled-back result still carries the read-back block.
    private static ScriptedTool Writer(bool committed) =>
        new(
            "create_schedule",
            handler: args => IsDryRun(args)
                ? ToolResult.Ok(
                    Parse("{\"dryRun\":true,\"changed\":[{\"id\":1}],\"skipped\":[],\"failed\":[]," +
                        "\"total\":1,\"affected\":0}"),
                    "preview")
                : committed
                    ? ToolResult.Ok(
                        Parse("{\"dryRun\":false,\"changed\":[{\"id\":1}],\"skipped\":[],\"failed\":[]," +
                            "\"total\":1,\"affected\":1," + ToolReadBackBlock + "}"),
                        "applied", 1)
                    : ToolResult.Fail(
                        "transaction_rolled_back",
                        "The transaction was rolled back. Nothing was changed in the model.",
                        data: Parse("{\"dryRun\":false,\"changed\":[],\"skipped\":[],\"failed\":[{\"id\":1}]," +
                            "\"total\":1,\"affected\":0," + ToolReadBackBlock + "}")),
            schema: WriteSchema);

    private static bool IsDryRun(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty("dryRun", out var dryRun)
        && dryRun.ValueKind == JsonValueKind.True;

    [Fact]
    public async Task Apply_ReadBackFailed_ReportsMismatch_NotVerificationNotRequested()
    {
        // The read-back tool itself failed: the declared expectation was never
        // compared. Before the fix the message said "verification not
        // requested" next to mismatches=1 (ORCH-6 scenario).
        var tool = CreateTool(ScheduleWriter(), FailingReader());

        var result = await ApplyAsync(tool, PlanWithReadBack.Replace("READER", "get_schedule_definition"));

        Assert.True(result.Success);
        Assert.Contains("1 mismatch(es)", result.Message, StringComparison.Ordinal);
        Assert.Contains("0 verified", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("verification not requested", result.Message, StringComparison.Ordinal);

        var planReadBack = result.Data!.Value.GetProperty("planReadBack");
        Assert.Equal(1, planReadBack.GetProperty("declared").GetInt32());
        Assert.Equal(0, planReadBack.GetProperty("expectationsChecked").GetInt32());
        Assert.Equal(1, planReadBack.GetProperty("mismatches").GetInt32());
        Assert.Equal(1, result.Data.Value.GetProperty("steps")[0]
            .GetProperty("planReadBack").GetProperty("declared").GetInt32());
    }

    [Fact]
    public async Task Apply_ReadBackFailed_HintSaysTheChangesAreNotVerified()
    {
        var tool = CreateTool(ScheduleWriter(), FailingReader());

        var result = await ApplyAsync(tool, PlanWithReadBack.Replace("READER", "get_schedule_definition"));

        var hint = result.Data!.Value.GetProperty("hint").GetString()!;
        Assert.Contains("could not run", hint, StringComparison.Ordinal);
        Assert.Contains("not verified", hint, StringComparison.Ordinal);
        Assert.Contains("step(s) 1", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("No plan read-back expectations", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("undo_last", hint, StringComparison.Ordinal);

        var planReadBack = result.Data.Value.GetProperty("planReadBack");
        Assert.Equal(new[] { "1" }, planReadBack.GetProperty("failedReadBackSteps")
            .EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Fact]
    public async Task Apply_ReadBackToolNotRegistered_ReportsMismatch()
    {
        var tool = CreateTool(ScheduleWriter());

        var result = await ApplyAsync(tool, PlanWithReadBack.Replace("READER", "missing_read_tool"));

        Assert.True(result.Success);
        Assert.Contains("1 mismatch(es)", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("verification not requested", result.Message, StringComparison.Ordinal);
        var mismatches = result.Data!.Value.GetProperty("mismatches");
        Assert.Equal(1, mismatches.GetArrayLength());
        Assert.Contains("is not registered", mismatches[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ValueMismatch_ReportsMismatchCount()
    {
        // A read-back that ran and found a differing value keeps the familiar
        // verified / mismatch line.
        var reader = new ScriptedTool(
            "get_schedule_definition",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(Parse("""{"sortGroupFieldCount":1}"""), "read"),
            schema: ReadSchema);
        var tool = CreateTool(ScheduleWriter(), reader);

        var result = await ApplyAsync(tool, PlanWithReadBack.Replace("READER", "get_schedule_definition"));

        Assert.True(result.Success);
        Assert.Contains("0 verified (0 tool-level), 1 mismatch(es)", result.Message, StringComparison.Ordinal);
        var planReadBack = result.Data!.Value.GetProperty("planReadBack");
        Assert.Equal(1, planReadBack.GetProperty("declared").GetInt32());
        Assert.Equal(1, planReadBack.GetProperty("expectationsChecked").GetInt32());
    }

    [Fact]
    public async Task Apply_ReadBackExecutedWithoutExpectations_ReportsVerified()
    {
        // A read-back tool was called (no expectations declared): verification
        // was requested, so the message must not claim otherwise.
        var reader = new ScriptedTool(
            "get_schedule_definition",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(Parse("""{"sortGroupFieldCount":1}"""), "read"),
            schema: ReadSchema);
        var tool = CreateTool(ScheduleWriter(), reader);
        var plan = PlanWithReadBack
            .Replace("READER", "get_schedule_definition")
            .Replace(",\"readBackExpectations\":[{\"path\":\"sortGroupFieldCount\",\"expected\":\"2\"}]", string.Empty);

        var result = await ApplyAsync(tool, plan);

        Assert.True(result.Success);
        Assert.DoesNotContain("verification not requested", result.Message, StringComparison.Ordinal);
        Assert.Contains("1 verified", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ToolRolledBack_FailureReportCarriesNoToolReadBack()
    {
        // A write tool outside AgentWriteToolBase may still leave its read-back
        // block on a rolled-back result. The failure report must not show it:
        // nothing the block says it verified is in the model (review VA-6).
        var tool = CreateTool(Writer(committed: false));

        var result = await ApplyAsync(tool, PlanWithoutReadBack);

        Assert.False(result.Success);
        Assert.Equal("plan_apply_failed", result.ErrorCode);
        var step = result.Data!.Value.GetProperty("steps")[0];
        Assert.Equal("failed", step.GetProperty("status").GetString());
        Assert.True(
            !step.TryGetProperty("toolReadBack", out var readBack)
            || readBack.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Apply_CommittedToolReadBack_IsSummedAndShown()
    {
        // The guard above must keep the block of a tool that did commit.
        var tool = CreateTool(Writer(committed: true));

        var result = await ApplyAsync(tool, PlanWithoutReadBack);

        Assert.True(result.Success);
        Assert.Contains("1 verified (2 tool-level), 0 mismatch(es)", result.Message, StringComparison.Ordinal);
        var planReadBack = result.Data!.Value.GetProperty("planReadBack");
        Assert.Equal(2, planReadBack.GetProperty("toolVerified").GetInt32());
        Assert.Equal(2, result.Data.Value.GetProperty("steps")[0]
            .GetProperty("toolReadBack").GetProperty("verified").GetInt32());
    }
}
