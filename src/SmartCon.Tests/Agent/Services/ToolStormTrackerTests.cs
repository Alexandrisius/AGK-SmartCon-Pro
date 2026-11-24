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
        Assert.Contains("calls of start_job in round(s) 1, 2, 3", hint, StringComparison.Ordinal);
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
    public void Observe_CallsThatDifferInMoreThanTheObject_AreNoSeries()
    {
        // Three filters, one per view: the view is the TARGET of each call and
        // every filter is a different piece of work. Not one job fed view by view.
        const string schema = @"{""type"":""object"",""properties"":{" +
            @"""name"":{""type"":""string""},""viewIds"":{""type"":""array"",""items"":{""type"":""integer""}}," +
            @"""dryRun"":{""type"":""boolean""}}}";
        var registry = new FakeToolRegistry(new ScriptedTool("create_view_filter", schema: schema));
        var ok = ToolResult.Ok(null, "done");
        var tracker = new ToolStormTracker();

        for (var index = 1; index <= 4; index++)
        {
            var arguments = "{\"name\":\"Filter " + index + "\",\"viewIds\":[" + index * 100 + "]}";
            Assert.Empty(tracker.Observe(registry, "create_view_filter", arguments, ok, index));
        }
    }

    [Fact]
    public void Observe_SameCallWithAnotherObject_SuggestsBatchingWhateverTheKeyOrder()
    {
        const string schema = @"{""type"":""object"",""properties"":{" +
            @"""newName"":{""type"":""string""},""viewIds"":{""type"":""array"",""items"":{""type"":""integer""}}," +
            @"""dryRun"":{""type"":""boolean""}}}";
        var registry = new FakeToolRegistry(new ScriptedTool("rename_views", schema: schema));
        var ok = ToolResult.Ok(null, "done");
        var tracker = new ToolStormTracker();

        // Same rest in two spellings: only the object handed over changes.
        Assert.Empty(tracker.Observe(
            registry, "rename_views", "{\"newName\":\"{0}_Plan\",\"viewIds\":[10]}", ok, 1));
        Assert.Empty(tracker.Observe(
            registry, "rename_views", "{\"viewIds\": [ 11 ], \"newName\":\"{0}_Plan\"}", ok, 2));

        var hint = Assert.Single(
            tracker.Observe(registry, "rename_views", "{\"viewIds\":[12],\"newName\":\"{0}_Plan\"}", ok, 3));
        Assert.Contains("calls of rename_views in round(s) 1, 2, 3", hint, StringComparison.Ordinal);
        Assert.Contains("batch them", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_OneConditionOrAPageOfOne_IsNotAnObjectList()
    {
        // The night run: a filter, a projection or a page size of one fed to a
        // tool three times is three different requests, not a batch cut to one.
        const string querySchema = @"{""type"":""object"",""properties"":{" +
            @"""conditions"":{""type"":""array"",""items"":{""type"":""object""}}," +
            @"""categories"":{""type"":""array"",""items"":{""type"":""string""}}," +
            @"""fields"":{""type"":""array"",""items"":{""type"":""string""}}," +
            @"""limit"":{""type"":""integer"",""default"":50}}}";
        const string summarizeSchema = @"{""type"":""object"",""properties"":{" +
            @"""categories"":{""type"":""array"",""items"":{""type"":""string""}}," +
            @"""topN"":{""type"":""integer"",""default"":20}}}";
        var registry = new FakeToolRegistry(
            new ScriptedTool("query_elements", schema: querySchema),
            new ScriptedTool("summarize_elements", schema: summarizeSchema));
        var ok = ToolResult.Ok(null, "done");
        var tracker = new ToolStormTracker();

        for (var index = 1; index <= 4; index++)
        {
            Assert.Empty(tracker.Observe(
                registry, "query_elements",
                "{\"limit\":1,\"conditions\":[{\"parameter\":\"Mark\",\"value\":\"" + index + "\"}]," +
                "\"categories\":[\"Pipes\"],\"fields\":[\"name\"]}",
                ok, index));
            Assert.Empty(tracker.Observe(
                registry, "summarize_elements", "{\"categories\":[\"Group " + index + "\"],\"topN\":1}", ok, index));
        }
    }

    [Fact]
    public void Observe_ExclusionListWithOneEntry_IsNotAnObjectList()
    {
        const string schema = @"{""type"":""object"",""properties"":{" +
            @"""templateId"":{""type"":""integer""}," +
            @"""exceptViewIds"":{""type"":""array"",""items"":{""type"":""integer""}}}}";
        var registry = new FakeToolRegistry(new ScriptedTool("apply_view_template", schema: schema));
        var ok = ToolResult.Ok(null, "done");
        var tracker = new ToolStormTracker();

        for (var index = 1; index <= 4; index++)
        {
            Assert.Empty(tracker.Observe(
                registry, "apply_view_template",
                "{\"templateId\":7,\"exceptViewIds\":[" + index + "]}", ok, index));
        }
    }

    [Fact]
    public void Observe_SameObjectAgain_IsLeftToTheIdenticalCallRule()
    {
        var registry = new FakeToolRegistry(new ScriptedTool("start_job", schema: JobSchema));
        var ok = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { done = 1 }), "changed every time");
        var tracker = new ToolStormTracker();

        for (var round = 1; round <= 4; round++)
        {
            // The same object each time: a repeat, not a walk over a batch.
            var hints = tracker.Observe(registry, "start_job", Single("a"), ok, round);
            Assert.DoesNotContain(hints, hint => hint.Contains("batch them", StringComparison.Ordinal));
        }
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
    public void Observe_PlanFailingOnDifferentSteps_IsNotTheSameError()
    {
        // The stress run: three plan previews failed with plan_dry_run_failed but
        // on different steps — the code alone called that "the same error" and
        // the model started changing a path that was fine (S.42).
        var tracker = new ToolStormTracker();

        for (var round = 1; round <= 4; round++)
        {
            var failure = ToolResult.Fail(
                "plan_dry_run_failed", "Step dry run failed.",
                data: JsonSerializer.SerializeToElement(
                    new { failedStepId = "s" + round, phase = "preview" }));

            Assert.Empty(tracker.Observe(null, "execute_plan", "{\"steps\":[...]}", failure, round));
        }
    }

    [Fact]
    public void Observe_PlanFailingOnTheSameStep_NamesTheStep()
    {
        var tracker = new ToolStormTracker();

        for (var round = 1; round <= 3; round++)
        {
            var failure = ToolResult.Fail(
                "plan_dry_run_failed", "Step 's2' dry run failed.",
                data: JsonSerializer.SerializeToElement(new { failedStepId = "s2" }));

            var hints = tracker.Observe(null, "execute_plan", "{\"steps\":[...]}", failure, round);
            if (round < 3)
            {
                Assert.Empty(hints);
                continue;
            }

            var hint = Assert.Single(hints);
            Assert.Contains(
                "already failed in round(s) 1, 2 with the same error (plan_dry_run_failed on step 's2')",
                hint, StringComparison.Ordinal);
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
        Assert.Contains("choose another path", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_SameInvalidArguments_NeverSaysThatChangingThemWillNotHelp()
    {
        // The night run: the model fixed one broken path after another
        // (items[214].value, then items[149].value) and was getting closer —
        // while the hint told it that changing arguments would not help.
        var tracker = new ToolStormTracker();

        for (var round = 1; round <= 3; round++)
        {
            var hints = tracker.Observe(
                null, "set_parameters", "{\"items\":[{\"value\":" + round + "}]}",
                ToolResult.Fail("invalid_arguments", "$.items[0].value: expected a string"), round);
            if (round < 3)
            {
                Assert.Empty(hints);
                continue;
            }

            var hint = Assert.Single(hints);
            Assert.Contains("failed in round(s) 1, 2 with invalid arguments", hint, StringComparison.Ordinal);
            Assert.Contains("rebuild the call from it", hint, StringComparison.Ordinal);
            Assert.DoesNotContain("will not help", hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Observe_PollOfUnfinishedWork_AdvisesWaitingNotChangingTheCall()
    {
        var tracker = new ToolStormTracker();
        var payload = JsonSerializer.SerializeToElement(new { mustVerify = true, done = 50, total = 52 });

        tracker.Observe(null, "get_status", "{}", ToolResult.Ok(payload, "Running"), 1);
        Assert.Empty(tracker.Observe(null, "get_status", "{}", ToolResult.Ok(payload, "Running"), 2));
        var hint = Assert.Single(tracker.Observe(null, "get_status", "{}", ToolResult.Ok(payload, "Running"), 3));

        Assert.Contains("still in progress", hint, StringComparison.Ordinal);
        Assert.Contains("wait longer", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("change the arguments", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_HostTimeoutsInARow_AreNamedFromTheSecondOne()
    {
        var tracker = new ToolStormTracker();
        var stuck = ToolResult.Fail(
            "tool_timeout", "still running",
            data: JsonSerializer.SerializeToElement(new { stillRunning = true }));

        Assert.Empty(tracker.Observe(null, "write_tool", "{\"a\":1}", stuck, 4));
        var second = Assert.Single(tracker.Observe(null, "write_tool", "{\"a\":2}", stuck, 5));
        var third = Assert.Single(tracker.Observe(null, "write_tool", "{\"a\":3}", stuck, 6));

        Assert.Contains("2 tool calls in a row timed out", second, StringComparison.Ordinal);
        Assert.Contains("since round 4", second, StringComparison.Ordinal);
        Assert.Contains("3 tool calls in a row timed out", third, StringComparison.Ordinal);
        Assert.Contains("tell the user what is blocked", third, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_AnyOtherResult_EndsTheHostTimeoutStreak()
    {
        var tracker = new ToolStormTracker();
        var stuck = ToolResult.Fail(
            "tool_timeout", "still running",
            data: JsonSerializer.SerializeToElement(new { stillRunning = true }));

        tracker.Observe(null, "write_tool", "{\"a\":1}", stuck, 1);
        tracker.Observe(null, "read_tool", "{\"a\":2}", stuck, 2);
        tracker.Observe(null, "read_tool", "{\"a\":3}", ToolResult.Ok(null, "answered"), 3);

        // The streak starts over: one more timeout is the first of a new series.
        Assert.Empty(tracker.Observe(null, "write_tool", "{\"a\":4}", stuck, 4));
    }

    [Fact]
    public void Observe_AfterForgetResultHistory_AReadPageIsFreshAgain()
    {
        // A compaction removed the earlier results from the model's side: the
        // hint that a page is "already in the conversation above" would be false.
        var tracker = new ToolStormTracker();
        var registry = new FakeToolRegistry(new ScriptedTool("summarize_elements"));
        var payload = JsonSerializer.SerializeToElement(new { groups = new[] { new { name = "a" } } });

        tracker.Observe(null, "read_tool_result", "{\"handle\":\"call_1\"}", Page(0), 1);
        tracker.Observe(null, "read_tool_result", "{\"handle\":\"call_1\"}", Page(0), 2);
        tracker.Observe(registry, "summarize_elements", "{\"topN\":50}", ToolResult.Ok(payload, "s"), 2);
        tracker.Observe(registry, "summarize_elements", "{\"topN\":50}", ToolResult.Ok(payload, "s"), 3);

        tracker.ForgetResults();

        Assert.Empty(tracker.Observe(null, "read_tool_result", "{\"handle\":\"call_1\"}", Page(0), 4));
        Assert.Empty(tracker.Observe(registry, "summarize_elements", "{\"topN\":50}", ToolResult.Ok(payload, "s"), 4));
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
    public void Observe_SixIdenticalSuccessfulCalls_SamePayload_WarnsFromTheThirdOn()
    {
        // ORCH-12: successful repeats are a storm too.
        var tracker = new ToolStormTracker();
        var ok = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { groups = new[] { new { name = "a", count = 3 } } }),
            "Summarized 3 element(s).");

        for (var round = 1; round <= 6; round++)
        {
            var hints = tracker.Observe(null, "summarize_elements", "{\"topN\":50}", ok, round);
            if (round < 3)
            {
                Assert.Empty(hints);
                continue;
            }

            var hint = Assert.Single(hints);
            Assert.Contains(
                "already returned this result in round(s) " +
                string.Join(", ", Enumerable.Range(1, round - 1)), hint, StringComparison.Ordinal);
            Assert.Contains("change the arguments or choose another path", hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Observe_SameCallAnotherPayload_IsNotARepeat()
    {
        // A poll whose counters moved is another result, even with the same message.
        var tracker = new ToolStormTracker();
        for (var round = 1; round <= 4; round++)
        {
            var moved = ToolResult.Ok(
                JsonSerializer.SerializeToElement(new { done = round }), "Running");
            Assert.Empty(tracker.Observe(null, "get_status", "{}", moved, round));
        }
    }

    [Fact]
    public void Observe_SameCallSamePayloadAnotherMessage_IsARepeat()
    {
        var tracker = new ToolStormTracker();
        var payload = JsonSerializer.SerializeToElement(new { value = 1 });
        tracker.Observe(null, "get_state", "{}", ToolResult.Ok(payload, "took 12 ms"), 1);

        var hints = tracker.Observe(null, "get_state", "{}", ToolResult.Ok(payload, "took 15 ms"), 2);

        Assert.Empty(hints);
        var third = tracker.Observe(null, "get_state", "{}", ToolResult.Ok(payload, "took 11 ms"), 3);
        Assert.Contains(
            "already returned this result in round(s) 1, 2",
            Assert.Single(third),
            StringComparison.Ordinal);
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

    [Fact]
    public void Observe_SameErrorBeforeACompaction_IsNamedWithoutRoundsTheModelCannotSee()
    {
        // The storm does not depend on what the model sees (WB review r2, P3-3):
        // the third failure is still a storm, but a compaction removed the first
        // two from the model's side, so the hint cannot send it back to them.
        var tracker = new ToolStormTracker();
        var failure = ToolResult.Fail("file_missing", "The file is not configured.");

        tracker.Observe(null, "list_params", "{\"a\":1}", failure, 1);
        tracker.Observe(null, "list_params", "{\"a\":2}", failure, 2);
        tracker.ForgetResults();

        var hint = Assert.Single(tracker.Observe(null, "list_params", "{\"a\":3}", failure, 9));

        Assert.Contains(
            "already failed 2 time(s) before the last compaction with the same error (file_missing)",
            hint, StringComparison.Ordinal);
        Assert.DoesNotContain("round(s)", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Read the reason once more", hint, StringComparison.Ordinal);
        Assert.Contains("choose another path", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_SameErrorAcrossACompaction_ListsOnlyTheRoundsTheModelSees()
    {
        var tracker = new ToolStormTracker();
        var failure = ToolResult.Fail("file_missing", "The file is not configured.");

        tracker.Observe(null, "list_params", "{\"a\":1}", failure, 1);
        tracker.Observe(null, "list_params", "{\"a\":2}", failure, 2);
        tracker.ForgetResults();
        tracker.Observe(null, "list_params", "{\"a\":3}", failure, 9);

        var hint = Assert.Single(tracker.Observe(null, "list_params", "{\"a\":4}", failure, 10));

        Assert.Contains(
            "already failed in round(s) 9 and 2 time(s) before the last compaction with the same error (file_missing)",
            hint, StringComparison.Ordinal);
        Assert.Contains("Read the reason once more and choose another path", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Observe_SingleObjectCallsAcrossACompaction_ListsOnlyTheRoundsTheModelSees()
    {
        var registry = new FakeToolRegistry(new ScriptedTool("start_job", schema: JobSchema));
        var ok = ToolResult.Ok(null, "done");
        var tracker = new ToolStormTracker();

        tracker.Observe(registry, "start_job", Single("a"), ok, 1);
        tracker.Observe(registry, "start_job", Single("b"), ok, 2);
        tracker.ForgetResults();

        var hint = Assert.Single(tracker.Observe(registry, "start_job", Single("c"), ok, 9));

        Assert.Contains(
            "calls of start_job in round(s) 9 and 2 time(s) before the last compaction: this tool takes several items",
            hint, StringComparison.Ordinal);
    }
}
