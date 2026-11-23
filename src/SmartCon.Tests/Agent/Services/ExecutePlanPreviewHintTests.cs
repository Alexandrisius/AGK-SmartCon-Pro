using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Hint of the execute_plan preview (review r1 #4). The report renames only the
/// ids a dry-run result marks as created (the created object's own id inside a
/// created* block, wouldCreate*Id); other ids of a creating step —
/// changed[].id — stay as they are. Every preview hint has
/// to say which ids are renamed and that the unrenamed ones are dry-run ids too,
/// in all three branches: complete, with skipped dependent steps and failed.
/// </summary>
public sealed class ExecutePlanPreviewHintTests
{
    private const string Schema =
        @"{""type"":""object"",""properties"":{""items"":{""type"":""array""},""dryRun"":{""type"":""boolean""}}," +
        @"""additionalProperties"":false}";

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<(string Hint, JsonElement Data)> PreviewAsync(ScriptedTool tool, string plan)
    {
        var result = await new ExecutePlanTool(
            () => new FakeToolRegistry(tool), new ImmediateAwaitableEvent(), new RecordingAuditLog())
            .ExecuteAsync(
                new AgentToolContext(new ImmediateAwaitableEvent(), "doc", Guid.Empty),
                Parse(plan),
                CancellationToken.None);

        return (result.Data!.Value.GetProperty("hint").GetString()!, result.Data.Value);
    }

    private static ScriptedTool ScheduleWriter() => new(
        "create_schedule",
        handler: _ => ToolResult.Ok(
            Parse("""
                {"dryRun":true,"changed":[{"id":1733593,"name":"Report","newValue":"Report"}],
                 "skipped":[],"failed":[],"total":1,"affected":1,
                 "created":{"scheduleId":1733593,"name":"Report"}}
                """),
            "ok", 1),
        schema: Schema);

    private const string SingleStep =
        "{\"goal\":\"g\",\"steps\":[{\"stepId\":\"1\",\"toolName\":\"create_schedule\",\"arguments\":{\"items\":[1]}}]}";

    private static void AssertNamesBothKindsOfDryRunIds(string hint)
    {
        Assert.Contains("Dry-run ids do not survive apply", hint, StringComparison.Ordinal);
        // What the report renames.
        Assert.Contains("created[].previewViewId", hint, StringComparison.Ordinal);
        Assert.Contains("created.previewScheduleId", hint, StringComparison.Ordinal);
        Assert.Contains("wouldCreate*Id", hint, StringComparison.Ordinal);
        // What it does not rename, although the ids are dry-run ids too.
        Assert.Contains("changed[].id", hint, StringComparison.Ordinal);
        Assert.Contains("Never reuse them", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletePreview_HintNamesRenamedAndUnrenamedDryRunIds()
    {
        // create_schedule reports the new schedule in created{scheduleId} — renamed
        // to previewScheduleId — and the changed[] card duplicating it is counted,
        // not repeated.
        var (hint, data) = await PreviewAsync(ScheduleWriter(), SingleStep);

        Assert.False(data.GetProperty("previewIncomplete").GetBoolean());
        var items = data.GetProperty("steps")[0].GetProperty("items");
        Assert.Equal(1733593, items.GetProperty("created").GetProperty("previewScheduleId").GetInt64());
        Assert.Equal(1, items.GetProperty("duplicatesInCreated").GetInt32());
        AssertNamesBothKindsOfDryRunIds(hint);
    }

    [Fact]
    public async Task PreviewWithSkippedDependentStep_HintAlsoNamesDryRunIds()
    {
        // The previewIncomplete branch is exactly where the model chains ids
        // into dependent steps; the warning used to be missing there.
        const string plan =
            "{\"goal\":\"g\",\"steps\":[" +
            "{\"stepId\":\"a\",\"toolName\":\"create_schedule\",\"arguments\":{\"items\":[1]}}," +
            "{\"stepId\":\"b\",\"toolName\":\"create_schedule\",\"arguments\":{\"items\":[2]},\"dependsOn\":[\"a\"]}]}";

        var (hint, data) = await PreviewAsync(ScheduleWriter(), plan);

        Assert.True(data.GetProperty("previewIncomplete").GetBoolean());
        Assert.Contains("preview_skipped", hint, StringComparison.Ordinal);
        Assert.Contains("only after explicit approval", hint, StringComparison.Ordinal);
        AssertNamesBothKindsOfDryRunIds(hint);
    }

    [Fact]
    public async Task FailedPreview_HintAlsoNamesDryRunIds()
    {
        // A failed preview (fidelity mismatch) reports the same steps[] with the
        // same ids, so its hint carries the warning as well.
        var tool = new ScriptedTool(
            "clone_schedule",
            handler: _ => ToolResult.Ok(
                Parse("""
                    {"dryRun":true,"changed":[{"id":777,"name":"Report","newValue":"Report"}],
                     "skipped":[],"failed":[],"total":1,"affected":1,
                     "created":{"scheduleId":777,"name":"Report"}}
                    """),
                "ok", 1),
            schema: Schema);
        var plan = SingleStep
            .Replace("create_schedule", "clone_schedule")
            .Replace("\"arguments\":{\"items\":[1]}}", "\"arguments\":{\"items\":[1]},\"expectedChanges\":5}");

        var (hint, data) = await PreviewAsync(tool, plan);

        Assert.Equal("plan_fidelity_mismatch", data.GetProperty("errorCode").GetString());
        Assert.Contains("Fix the failed step and preview again", hint, StringComparison.Ordinal);
        var created = data.GetProperty("steps")[0].GetProperty("items").GetProperty("created");
        Assert.Equal(777, created.GetProperty("previewScheduleId").GetInt64());
        AssertNamesBothKindsOfDryRunIds(hint);
    }
}
