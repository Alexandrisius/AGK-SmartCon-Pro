using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Pure logic behind the call-storm hints: what counts as the same call, the
/// same page and a single-item series, and what is deliberately not a storm.
/// </summary>
public sealed class ToolStormTrackerTests
{
    private const string JobSchema =
        @"{""type"":""object"",""properties"":{" +
        @"""maxItems"":{""type"":""integer"",""default"":1000}," +
        @"""names"":{""type"":""array"",""items"":{""type"":""string""}}," +
        @"""additionalProperties"":false}}";

    private const string PlanSchema =
        @"{""type"":""object"",""properties"":{" +
        @"""steps"":{""type"":""array"",""items"":{""type"":""object""}}}," +
        @"""additionalProperties"":false}";

    private static ToolResult Page(int offset) =>
        ToolResult.Ok(
            JsonSerializer.SerializeToElement(
                new { handle = "call_1", path = "data.views", offset, limit = 8000 }),
            "Returned 8000 of 20000 characters.");

    [Fact]
    public void Observe_SamePageReadAgain_NamesTheEarlierRound()
    {
        var tracker = new ToolStormTracker();

        var first = tracker.Observe(null, "read_tool_result", "{\"handle\":\"call_1\"}", Page(0), 2);
        // A second spelling of the same page: the effective offset/limit come from the result.
        var repeat = tracker.Observe(
            null, "read_tool_result", "{\"handle\":\"call_1\",\"offset\":0,\"limit\":8000}", Page(0), 4);
        var nextPage = tracker.Observe(
            null, "read_tool_result", "{\"handle\":\"call_1\",\"offset\":8000}", Page(8000), 5);

        Assert.Empty(first);
        var hint = Assert.Single(repeat);
        Assert.Contains("already read in round(s) 2", hint, StringComparison.Ordinal);
        Assert.Contains("path data.views", hint, StringComparison.Ordinal);
        Assert.Contains("offset 0, limit 8000", hint, StringComparison.Ordinal);
        Assert.Empty(nextPage);
    }

    [Fact]
    public void Observe_FailedRead_IsNotTreatedAsARepeatedPage()
    {
        var tracker = new ToolStormTracker();
        var failure = ToolResult.Fail("result_not_found", "No stored result for handle 'call_1'.");
        tracker.Observe(null, "read_tool_result", "{\"handle\":\"call_1\"}", failure, 1);

        var hints = tracker.Observe(null, "read_tool_result", "{\"handle\":\"call_1\"}", failure, 2);

        // The second identical failure is still one below the storm threshold.
        Assert.Empty(hints);
    }

    [Fact]
    public void Observe_ThreeCallsCappingABatchArgumentToOne_SuggestsBatching()
    {
        var registry = new FakeToolRegistry(new ScriptedTool("start_job", schema: JobSchema));
        var ok = ToolResult.Ok(null, "done");
        var tracker = new ToolStormTracker();

        // Distinct arguments: the identical-call rule must not answer first.
        Assert.Empty(tracker.Observe(registry, "start_job", Single("a"), ok, 1));
        Assert.Empty(tracker.Observe(registry, "start_job", Single("b"), ok, 2));
        var hints = tracker.Observe(registry, "start_job", Single("c"), ok, 3);

        var hint = Assert.Single(hints);
        Assert.Contains("round(s) 1, 2, 3", hint, StringComparison.Ordinal);
        Assert.Contains("this tool takes several items in one call; batch them", hint, StringComparison.Ordinal);
    }

    private static string Single(string name) =>
        "{\"maxItems\":1,\"names\":[\"" + name + "\"]}";

    [Fact]
    public void Observe_BatchedCallInBetween_EndsTheSeries()
    {
        var registry = new FakeToolRegistry(new ScriptedTool("start_job", schema: JobSchema));
        var ok = ToolResult.Ok(null, "done");
        var tracker = new ToolStormTracker();

        tracker.Observe(registry, "start_job", Single("a"), ok, 1);
        tracker.Observe(registry, "start_job", Single("b"), ok, 2);
        tracker.Observe(registry, "start_job", "{\"names\":[\"a\",\"b\"]}", ok, 3);

        Assert.Empty(tracker.Observe(registry, "start_job", Single("c"), ok, 4));
    }

    [Fact]
    public void Observe_ToolWithoutABatchArgument_NeverGetsTheBatchHint()
    {
        var registry = new FakeToolRegistry(
            new ScriptedTool("get_element_details", schema: @"{""type"":""object"",""properties"":{""elementId"":{""type"":""integer""}}}"));
        var ok = ToolResult.Ok(null, "done");
        var tracker = new ToolStormTracker();

        for (var round = 1; round <= 5; round++)
        {
            var hints = tracker.Observe(
                registry, "get_element_details", "{\"elementId\":" + round + "}", ok, round);
            Assert.Empty(hints);
        }
    }

    [Fact]
    public void Observe_SingleStepPlans_AreNotASeries()
    {
        // A plan is applied by repeating the previewed call: pairs are the contract.
        var registry = new FakeToolRegistry(new ScriptedTool("execute_plan", schema: PlanSchema));
        var ok = ToolResult.Ok(null, "done");
        var tracker = new ToolStormTracker();

        for (var round = 1; round <= 6; round++)
        {
            var hints = tracker.Observe(
                registry, "execute_plan", "{\"steps\":[{\"stepId\":\"" + round + "\"}]}", ok, round);
            Assert.DoesNotContain(hints, hint => hint.Contains("batch them", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Observe_SameErrorWithDifferentArguments_CountsAsOneStorm()
    {
        var tracker = new ToolStormTracker();
        var failure = ToolResult.Fail("file_missing", "The file is not configured.");

        tracker.Observe(null, "list_params", "{\"a\":1}", failure, 1);
        tracker.Observe(null, "list_params", "{\"a\":2}", failure, 2);
        var hints = tracker.Observe(null, "list_params", "{\"a\":3}", failure, 3);

        var hint = Assert.Single(hints);
        Assert.Contains("already failed in round(s) 1, 2 with the same error (file_missing)", hint, StringComparison.Ordinal);
        Assert.Contains("changing arguments of this kind will not help", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_DifferentErrorCodes_DoNotAccumulate()
    {
        var tracker = new ToolStormTracker();
        for (var round = 1; round <= 4; round++)
        {
            var hints = tracker.Observe(
                null, "list_params", "{\"a\":" + round + "}",
                ToolResult.Fail("error_" + round, "different"), round);
            Assert.Empty(hints);
        }
    }

    [Fact]
    public void Observe_HarnessErrorCodes_AreLeftToTheirOwnGuidance()
    {
        var tracker = new ToolStormTracker();
        for (var round = 1; round <= 4; round++)
        {
            var hints = tracker.Observe(
                null, "write_tool", "{\"a\":" + round + "}",
                ToolResult.Fail("loop_detected", "blocked"), round);
            Assert.Empty(hints);
        }
    }

    [Fact]
    public void Observe_SameErrorInsideOneRound_ListsTheRoundOnce()
    {
        var tracker = new ToolStormTracker();
        var failure = ToolResult.Fail("file_missing", "The file is not configured.");

        // Three parallel calls of one assistant message all land in round 1.
        tracker.Observe(null, "list_params", "{\"a\":1}", failure, 1);
        tracker.Observe(null, "list_params", "{\"a\":2}", failure, 1);
        var hints = tracker.Observe(null, "list_params", "{\"a\":3}", failure, 1);

        var hint = Assert.Single(hints);
        Assert.Contains("in round(s) 1 with the same error", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("1, 1", hint, StringComparison.Ordinal);
    }
}
