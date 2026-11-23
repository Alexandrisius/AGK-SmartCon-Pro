using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Projection of a step's <c>items</c> in the execute_plan report (review r1,
/// findings 3, 5, 7 and 8): the preview prefix covers only the created
/// object's own id, the fidelity text agrees with the counters after the
/// created/changed
/// dedupe, "+N more" is reported whenever rows were cut, and the dedupe
/// drops only the cards of the created object.
/// </summary>
public sealed class ExecutePlanItemsProjectionTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ExecutePlanTool CreateTool(ScriptedTool tool) =>
        new(() => new FakeToolRegistry(tool), new ImmediateAwaitableEvent(),
            new RecordingAuditLog());

    private static Task<ToolResult> PreviewAsync(ScriptedTool tool, string plan) =>
        CreateTool(tool).ExecuteAsync(
            new AgentToolContext(new ImmediateAwaitableEvent(), "doc", Guid.Empty),
            Parse(plan),
            CancellationToken.None);

    private static ScriptedTool Writer(string name, string payload) =>
        new(name, handler: _ => ToolResult.Ok(Parse(payload), "ok"), schema: Schema);

    private const string Schema =
        @"{""type"":""object"",""properties"":{""items"":{""type"":""array""},""dryRun"":{""type"":""boolean""}}," +
        @"""additionalProperties"":false}";

    private const string SingleStep =
        "{\"goal\":\"g\",\"steps\":[{\"stepId\":\"1\",\"toolName\":\"TOOL\",\"arguments\":{\"items\":[1]}}]}";

    private static JsonElement PreviewItems(ScriptedTool tool, string name, string plan = SingleStep)
    {
        var preview = PreviewAsync(tool, plan.Replace("TOOL", name)).GetAwaiter().GetResult();
        Assert.True(preview.Success, preview.Message);
        return preview.Data!.Value.GetProperty("steps")[0].GetProperty("items");
    }

    private static string[] Keys(JsonElement obj) =>
        obj.EnumerateObject().Select(property => property.Name).ToArray();

    // ── Finding 3: only the created object's own id gets the prefix ──────────

    [Fact]
    public void Preview_PrefixesOnlyTheCreatedObjectsOwnId_InBatchCreatedArrays()
    {
        // create_views_batch / create_floor_plans: levelId, templateId and
        // genLevelId point to objects that exist — the apply reuses them.
        const string payload =
            """
            {"dryRun":true,
             "changed":[{"name":"Plan A","newValue":"Plan A","reason":"Dry run: the plan view would be created."},
                        {"name":"Plan B","newValue":"Plan B","reason":"Dry run: the plan view would be created."}],
             "skipped":[],"failed":[],"total":2,"affected":2,"createdCount":2,
             "created":[{"viewId":null,"kind":"plan","name":"Plan A","levelId":100,"templateId":200,"genLevelId":100},
                        {"viewId":null,"kind":"plan","name":"Plan B","levelId":101,"templateId":null,"genLevelId":101}]}
            """;

        var items = PreviewItems(Writer("create_views_batch", payload), "create_views_batch");
        var created = items.GetProperty("created");

        Assert.Equal(
            new[] { "previewViewId", "kind", "name", "levelId", "templateId", "genLevelId" },
            Keys(created[0]));
        Assert.Equal(JsonValueKind.Null, created[0].GetProperty("previewViewId").ValueKind);
        Assert.Equal(100, created[0].GetProperty("levelId").GetInt64());
        Assert.Equal(200, created[0].GetProperty("templateId").GetInt64());
        Assert.Equal(100, created[0].GetProperty("genLevelId").GetInt64());

        Assert.Equal(
            new[] { "previewViewId", "kind", "name", "levelId", "templateId", "genLevelId" },
            Keys(created[1]));
        Assert.Equal(101, created[1].GetProperty("levelId").GetInt64());
        Assert.Equal(JsonValueKind.Null, created[1].GetProperty("previewViewId").ValueKind);

        Assert.Equal(2, items.GetProperty("createdCount").GetInt32());
    }

    [Fact]
    public void Preview_PrefixesOnlyTheCreatedObjectsOwnId_InASingleCreatedObject()
    {
        // clone_schedule shape: created{scheduleId, name}; a sibling category
        // id is a reference and must stay untouched.
        const string payload =
            """
            {"dryRun":true,
             "changed":[{"id":777,"name":"Report","newValue":"Report"}],
             "skipped":[],"failed":[],"total":1,"affected":1,
             "created":{"scheduleId":777,"name":"Report","categoryId":55}}
            """;

        var items = PreviewItems(Writer("clone_schedule", payload), "clone_schedule");
        var created = items.GetProperty("created");

        Assert.Equal(new[] { "previewScheduleId", "name", "categoryId" }, Keys(created));
        Assert.Equal(777, created.GetProperty("previewScheduleId").GetInt64());
        Assert.Equal(55, created.GetProperty("categoryId").GetInt64());
    }

    [Fact]
    public void Preview_PrefixesWouldCreateId_AndLeavesOtherKeysAlone()
    {
        // create_view_filter shape.
        const string payload =
            """
            {"dryRun":true,"changed":[{"name":"F1","oldValue":null,"newValue":"F1"}],
             "skipped":[],"failed":[],"total":1,"affected":1,
             "filterId":null,"wouldCreateId":555,"wouldCreate":true,"created":false,"filterName":"F1"}
            """;

        var items = PreviewItems(Writer("create_view_filter", payload), "create_view_filter");

        Assert.Equal(555, items.GetProperty("previewWouldCreateId").GetInt64());
        Assert.Equal(JsonValueKind.Null, items.GetProperty("filterId").ValueKind);
        Assert.True(items.GetProperty("wouldCreate").GetBoolean());
        Assert.False(items.GetProperty("created").GetBoolean());
        Assert.Equal("F1", items.GetProperty("filterName").GetString());
    }

    [Fact]
    public void Preview_OwnIdPrefix_DoesNotDependOnKeyOrder()
    {
        // Review r2, N6: the same created entry with the reference id serialized
        // FIRST — the own-id prefix must still land on scheduleId, not on the
        // first *Id in document order (it used to produce previewCategoryId).
        const string payload =
            """
            {"dryRun":true,
             "changed":[{"id":777,"name":"Report","newValue":"Report"}],
             "skipped":[],"failed":[],"total":1,"affected":1,
             "created":{"name":"Report","categoryId":55,"scheduleId":777}}
            """;

        var items = PreviewItems(Writer("clone_schedule", payload), "clone_schedule");
        var created = items.GetProperty("created");

        Assert.Equal(new[] { "name", "categoryId", "previewScheduleId" }, Keys(created));
        Assert.Equal(777, created.GetProperty("previewScheduleId").GetInt64());
        Assert.Equal(55, created.GetProperty("categoryId").GetInt64());
    }

    [Fact]
    public void Preview_OwnIdPrefix_UnambiguousSingleUnknownIdKey()
    {
        // An entry whose only *Id key is not in the known own-id list: the
        // single key is still the created object's id.
        const string payload =
            """
            {"dryRun":true,
             "changed":[{"id":42,"name":"Zone","newValue":"Zone"}],
             "skipped":[],"failed":[],"total":1,"affected":1,
             "created":{"zoneId":42,"name":"Zone","comment":"ref"}}
            """;

        var items = PreviewItems(Writer("write_tool", payload), "write_tool");
        var created = items.GetProperty("created");

        Assert.Equal(new[] { "previewZoneId", "name", "comment" }, Keys(created));
        Assert.Equal(42, created.GetProperty("previewZoneId").GetInt64());
    }

    [Fact]
    public async Task ApplyReport_NeverPrefixesIds()
    {
        const string payload =
            """
            {"dryRun":false,
             "changed":[{"id":9001,"name":"Plan A","newValue":"Plan A"}],
             "skipped":[],"failed":[],"total":1,"affected":1,
             "created":[{"viewId":9001,"kind":"plan","name":"Plan A","levelId":100,"templateId":200,"genLevelId":100}]}
            """;
        var tool = CreateTool(Writer("create_views_batch", payload));

        var apply = await tool.ExecuteAsync(
            new AgentToolContext(new ImmediateAwaitableEvent(), "doc", Guid.Empty) { WriteApproved = true },
            Parse(SingleStep.Replace("TOOL", "create_views_batch").Replace("\"steps\"", "\"dryRun\":false,\"steps\"")),
            CancellationToken.None);

        Assert.True(apply.Success, apply.Message);
        var created = apply.Data!.Value.GetProperty("steps")[0].GetProperty("items").GetProperty("created");
        Assert.Equal(
            new[] { "viewId", "kind", "name", "levelId", "templateId", "genLevelId" },
            Keys(created[0]));
        Assert.Equal(9001, created[0].GetProperty("viewId").GetInt64());
    }

    // ── Finding 5: fidelity text vs report counters ──────────────────────────

    [Fact]
    public async Task FidelityMismatch_TellsToTakeTheCountFromStepsChanged_NotItemsChanged()
    {
        // All three cards duplicate the created block, so items.changed[] is
        // empty after the dedupe while the tool counted 3. "Take the length of
        // changed[]" would send the model straight into the next mismatch.
        const string payload =
            """
            {"dryRun":true,
             "changed":[{"name":"A","newValue":"A"},{"name":"B","newValue":"B"},{"name":"C","newValue":"C"}],
             "skipped":[],"failed":[],"total":3,"affected":3,
             "created":[{"viewId":null,"name":"A"},{"viewId":null,"name":"B"},{"viewId":null,"name":"C"}]}
            """;
        var tool = Writer("create_views_batch", payload);

        var preview = await PreviewAsync(
            tool,
            SingleStep.Replace("TOOL", "create_views_batch").Replace("\"arguments\":{\"items\":[1]}}", "\"arguments\":{\"items\":[1]},\"expectedChanges\":1}"));

        Assert.False(preview.Success);
        Assert.Equal("plan_fidelity_mismatch", preview.ErrorCode);
        Assert.Contains("the tool counted 3 change(s)", preview.Message, StringComparison.Ordinal);
        Assert.Contains("steps[].changed", preview.Message, StringComparison.Ordinal);
        Assert.Contains("duplicatesInCreated", preview.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("length of changed[]", preview.Message, StringComparison.Ordinal);

        var step = preview.Data!.Value.GetProperty("steps")[0];
        Assert.Equal(3, step.GetProperty("changed").GetInt32());
        Assert.Equal(0, step.GetProperty("items").GetProperty("changed").GetArrayLength());
        Assert.Equal(3, step.GetProperty("items").GetProperty("duplicatesInCreated").GetInt32());
    }

    // ── Finding 7: "+N more" ─────────────────────────────────────────────────

    [Fact]
    public async Task StepError_ReportsTheCutTail_WhenExactlyMaxReasonsWereKept()
    {
        var failed = Enumerable.Range(1, 10)
            .Select(index => new { name = "row" + index, reason = "reason " + index })
            .ToArray();
        var tool = new ScriptedTool(
            "write_tool",
            handler: _ => ToolResult.Fail(
                "dry_run_failed",
                "batch failed",
                data: JsonSerializer.SerializeToElement(new { total = 0, failed })),
            schema: Schema);

        var preview = await PreviewAsync(tool, SingleStep.Replace("TOOL", "write_tool"));

        var reasons = preview.Data!.Value.GetProperty("steps")[0].GetProperty("error").GetProperty("failed");
        Assert.Equal(
            new[] { "reason 1", "reason 2", "reason 3", "+7 more" },
            reasons.EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Fact]
    public async Task StepError_NoTail_WhenNothingWasCut()
    {
        var failed = Enumerable.Range(1, 3)
            .Select(index => new { name = "row" + index, reason = "reason " + index })
            .ToArray();
        var tool = new ScriptedTool(
            "write_tool",
            handler: _ => ToolResult.Fail(
                "dry_run_failed",
                "batch failed",
                data: JsonSerializer.SerializeToElement(new { total = 0, failed })),
            schema: Schema);

        var preview = await PreviewAsync(tool, SingleStep.Replace("TOOL", "write_tool"));

        var reasons = preview.Data!.Value.GetProperty("steps")[0].GetProperty("error").GetProperty("failed");
        Assert.Equal(
            new[] { "reason 1", "reason 2", "reason 3" },
            reasons.EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    // ── Finding 8: dedupe identity is the created object, not any *Id ────────

    [Fact]
    public void Dedupe_KeepsModificationCardsThatOnlyReferenceTheCreatedId()
    {
        // Cards 9 and 11 are changes to OTHER objects that merely reference id
        // 777 (as their schedule, as their template); only the created object's
        // own card is the duplicate and must be dropped.
        const string payload =
            """
            {"dryRun":false,
             "changed":[{"id":777,"name":"Report","newValue":"Report"},
                        {"id":9,"name":"param name","scheduleId":777,"oldValue":"x","newValue":"y"},
                        {"id":11,"name":"View B","templateId":777}],
             "skipped":[],"failed":[],"total":3,"affected":3,
             "created":{"scheduleId":777,"name":"Report"}}
            """;

        var items = PreviewItems(Writer("clone_schedule", payload), "clone_schedule");
        var changed = items.GetProperty("changed");

        Assert.Equal(1, items.GetProperty("duplicatesInCreated").GetInt32());
        Assert.Equal(
            new long?[] { 9, 11 },
            changed.EnumerateArray().Select(card => card.TryGetProperty("id", out var id) ? id.GetInt64() : (long?)null).ToArray());
    }

    [Fact]
    public void Dedupe_DropsACardWithoutId_WhenTheCreatedObjectHasNoneEither()
    {
        // Dry run of a batch creator: neither the created entry nor its card
        // carries an id, so the name is the only identity — the card is still
        // recognised as the duplicate.
        const string payload =
            """
            {"dryRun":true,
             "changed":[{"name":"View A","newValue":"View A","reason":"Dry run."},
                        {"name":"param name","newValue":"y"}],
             "skipped":[],"failed":[],"total":2,"affected":2,
             "created":[{"viewId":null,"name":"View A"}]}
            """;

        var items = PreviewItems(Writer("create_views_batch", payload), "create_views_batch");
        var changed = items.GetProperty("changed");

        Assert.Equal(1, items.GetProperty("duplicatesInCreated").GetInt32());
        var kept = Assert.Single(changed.EnumerateArray());
        Assert.Equal("param name", kept.GetProperty("name").GetString());
    }

    [Fact]
    public void Dedupe_KeepsASameNamedCardOfAnotherObject()
    {
        // The created object and another changed object share a name: the id
        // decides, the same-named card of the other object stays.
        const string payload =
            """
            {"dryRun":false,
             "changed":[{"id":5,"name":"Shared","newValue":"Shared"},
                        {"id":9,"name":"Shared","oldValue":"before","newValue":"Shared"}],
             "skipped":[],"failed":[],"total":2,"affected":2,
             "created":{"viewId":5,"name":"Shared"}}
            """;

        var items = PreviewItems(Writer("clone_schedule", payload), "clone_schedule");
        var changed = items.GetProperty("changed");

        Assert.Equal(1, items.GetProperty("duplicatesInCreated").GetInt32());
        var kept = Assert.Single(changed.EnumerateArray());
        Assert.Equal(9, kept.GetProperty("id").GetInt64());
    }

    [Fact]
    public void Dedupe_KeepsTheCardWhenTheCreatedIdDiffersFromTheCardId()
    {
        const string payload =
            """
            {"dryRun":false,
             "changed":[{"id":5,"name":"Shared","newValue":"Shared"}],
             "skipped":[],"failed":[],"total":1,"affected":1,
             "created":{"scheduleId":777,"name":"Shared"}}
            """;

        var items = PreviewItems(Writer("clone_schedule", payload), "clone_schedule");

        Assert.False(items.TryGetProperty("duplicatesInCreated", out _));
        Assert.Equal(1, items.GetProperty("changed").GetArrayLength());
    }
}
