using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Report-shape gate of execute_plan (night2 findings): a failed step must
/// carry the nested tool's cause (C4-08), an apply without read-back must say
/// "verification not requested" (C4-18), a fidelity mismatch must name what
/// the tool actually counted (C7-04), preview ids of created objects must be
/// marked as preview (C7-05/C4-11) and an apply over unpreviewed dependent
/// steps must warn (C1-02).
/// </summary>
public sealed class ExecutePlanReportTests
{
    private static ExecutePlanTool CreateTool(ScriptedTool writeTool) =>
        new(() => new FakeToolRegistry(writeTool), new ImmediateAwaitableEvent(),
            new RecordingAuditLog());

    private static AgentToolContext Context(bool approved) =>
        new(new ImmediateAwaitableEvent(), "doc", Guid.Empty) { WriteApproved = approved };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ApplyArgs(string plan, string digest) =>
        plan.Replace("\"steps\"", "\"dryRun\":false,\"planDigest\":\"" + digest + "\",\"steps\"");

    [Fact]
    public async Task Preview_FailedStep_CarriesNestedToolCause()
    {
        // C4-08: the step error must expose the nested tool's code, message and
        // failed[] reasons instead of a bare "dry_run_failed".
        var writeTool = new ScriptedTool(
            "write_tool",
            handler: _ => ToolResult.Fail(
                "dry_run_failed",
                "Parameter is not a shared parameter",
                data: JsonSerializer.SerializeToElement(new
                {
                    total = 0,
                    failed = new object[]
                    {
                        new { name = "param name", reason = "not a shared parameter" }
                    }
                })));
        var tool = CreateTool(writeTool);

        var result = await tool.ExecuteAsync(
            Context(approved: false),
            Parse("{\"goal\":\"write\",\"steps\":[{\"stepId\":\"1\",\"toolName\":\"write_tool\",\"arguments\":{\"items\":[{\"viewId\":1}]}}]}"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_dry_run_failed", result.ErrorCode);
        var error = result.Data!.Value.GetProperty("steps")[0].GetProperty("error");
        Assert.Equal("dry_run_failed", error.GetProperty("code").GetString());
        Assert.Contains(
            "not a shared parameter",
            error.GetProperty("message").GetString(), StringComparison.Ordinal);
        var reasons = error.GetProperty("failed");
        Assert.Equal(JsonValueKind.Array, reasons.ValueKind);
        Assert.Contains(
            "not a shared parameter",
            reasons[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_WithoutReadBack_SaysVerificationNotRequested()
    {
        // C4-18: "0 verified" read as "checked and confirmed nothing".
        var writeTool = new ScriptedTool("write_tool");
        var tool = CreateTool(writeTool);

        var result = await tool.ExecuteAsync(
            Context(approved: true),
            Parse("{\"goal\":\"write\",\"dryRun\":false,\"steps\":[{\"stepId\":\"s1\",\"toolName\":\"write_tool\",\"arguments\":{\"items\":[{\"viewId\":1}]}}]}"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("verification not requested", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0 verified", result.Message, StringComparison.Ordinal);
        Assert.Contains(
            "verification not requested",
            result.Data!.Value.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FidelityMismatch_ListsWhatTheToolCounted()
    {
        // C7-04: the mismatch text must name the actual count and the counted
        // items so the model corrects expectedChanges on the first try.
        var writeTool = new ScriptedTool(
            "write_tool",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new object[]
                    {
                        new { name = "Sheet list", action = "schedule_created" },
                        new { name = "Mark", action = "field_added" },
                        new { name = "Category", action = "field_added" },
                        new { name = "Type", action = "field_added" },
                        new { action = "field_added" }
                    },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    total = 5,
                    affected = 5
                }),
                "five changes"));
        var tool = CreateTool(writeTool);

        var result = await tool.ExecuteAsync(
            Context(approved: false),
            Parse("{\"goal\":\"schedule\",\"steps\":[{\"stepId\":\"1\",\"toolName\":\"write_tool\",\"arguments\":{\"items\":[1]},\"expectedChanges\":1}]}"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_fidelity_mismatch", result.ErrorCode);
        Assert.Contains("the tool counted 5 change(s)", result.Message, StringComparison.Ordinal);
        Assert.Contains("'Sheet list' (schedule_created)", result.Message, StringComparison.Ordinal);
        Assert.Contains("+2 more", result.Message, StringComparison.Ordinal);
        Assert.Contains(
            "The counter is the length of changed[]",
            result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_MarksCreatedIdsAsPreview_AndApplyKeepsRealIds()
    {
        // C7-05/C4-11: a dry run rolls back, so created.scheduleId is not a real
        // element id. The preview must rename it with a preview prefix; the
        // apply report keeps the real id untouched.
        var writeTool = new ScriptedTool(
            "write_tool",
            handler: args =>
            {
                var dryRun = !(args.ValueKind == JsonValueKind.Object
                               && args.TryGetProperty("dryRun", out var flag)
                               && flag.ValueKind == JsonValueKind.False);
                return ToolResult.Ok(
                    JsonSerializer.SerializeToElement(new
                    {
                        dryRun,
                        changed = new[] { new { id = 1 } },
                        skipped = Array.Empty<object>(),
                        failed = Array.Empty<object>(),
                        total = 1,
                        affected = 1,
                        created = new { scheduleId = 777, name = "Report" }
                    }),
                    "ok", 1);
            });
        var tool = CreateTool(writeTool);
        const string plan =
            "{\"goal\":\"clone\",\"steps\":[{\"stepId\":\"1\",\"toolName\":\"write_tool\",\"arguments\":{\"items\":[1]}}]}";

        var preview = await tool.ExecuteAsync(
            Context(approved: false), Parse(plan), CancellationToken.None);
        Assert.True(preview.Success);
        var previewItems = preview.Data!.Value.GetProperty("steps")[0].GetProperty("items");
        var previewCreated = previewItems.GetProperty("created");
        Assert.Equal(777, previewCreated.GetProperty("previewScheduleId").GetInt64());
        Assert.False(previewCreated.TryGetProperty("scheduleId", out _));
        Assert.Contains(
            "preview prefix",
            preview.Data.Value.GetProperty("hint").GetString(), StringComparison.Ordinal);

        var digest = preview.Data.Value.GetProperty("planDigest").GetString()!;
        var apply = await tool.ExecuteAsync(
            Context(approved: true), Parse(ApplyArgs(plan, digest)), CancellationToken.None);
        Assert.True(apply.Success);
        var appliedItems = apply.Data!.Value.GetProperty("steps")[0].GetProperty("items");
        var appliedCreated = appliedItems.GetProperty("created");
        Assert.Equal(777, appliedCreated.GetProperty("scheduleId").GetInt64());
        Assert.False(appliedCreated.TryGetProperty("previewScheduleId", out _));
    }

    [Fact]
    public async Task Apply_OverUnpreviewedDependentStep_Warns()
    {
        // C1-02: a false dependsOn removes a step from the preview; the apply
        // report must say so instead of passing silently.
        var writeTool = new ScriptedTool("write_tool");
        var tool = CreateTool(writeTool);
        const string plan =
            "{\"goal\":\"two\",\"steps\":[" +
            "{\"stepId\":\"a\",\"toolName\":\"write_tool\",\"arguments\":{\"items\":[1]}}," +
            "{\"stepId\":\"b\",\"toolName\":\"write_tool\",\"arguments\":{\"items\":[2]},\"dependsOn\":[\"a\"]}]}";

        var apply = await tool.ExecuteAsync(
            Context(approved: true),
            Parse(plan.Replace("\"steps\"", "\"dryRun\":false,\"steps\"")),
            CancellationToken.None);

        Assert.True(apply.Success);
        Assert.True(apply.Data!.Value.GetProperty("previewIncomplete").GetBoolean());
        var warning = apply.Data.Value.GetProperty("warning").GetString();
        Assert.NotNull(warning);
        Assert.Contains("b", warning, StringComparison.Ordinal);
        Assert.Contains("without a preview", warning, StringComparison.Ordinal);
        Assert.Contains(
            "1 step(s) applied without preview",
            apply.Message, StringComparison.Ordinal);
        Assert.Contains(
            "without a preview",
            apply.Data.Value.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }
}
