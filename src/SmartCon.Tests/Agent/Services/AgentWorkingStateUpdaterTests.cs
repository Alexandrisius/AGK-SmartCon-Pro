using System.Text.Json;
using Xunit;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentWorkingStateUpdaterTests
{
    [Fact]
    public void UpdateFromUserText_SetsGoalOnlyWhenEmpty()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.UpdateFromUserText(state, "  Переименуй виды  ");
        AgentWorkingStateUpdater.UpdateFromUserText(state, "другая задача");

        Assert.Equal("Переименуй виды", state.Goal);
    }

    [Fact]
    public void UpdateFromUserText_CapsGoal()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.UpdateFromUserText(state, new string('x', 500));

        Assert.NotNull(state.Goal);
        Assert.Equal(AgentWorkingStateUpdater.MaxGoalLength + 1, state.Goal!.Length);
        Assert.EndsWith("…", state.Goal);
    }

    [Fact]
    public void UpdateFromUserText_ExtractsConstraintMarkers()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.UpdateFromUserText(
            state,
            "Переименуй виды.\n" +
            "Не трогай листы.\n" +
            "Only plans, without sheets.\n" +
            "Сделай красиво.");

        Assert.Contains("Не трогай листы", state.Constraints);
        Assert.Contains("Only plans, without sheets", state.Constraints);
        Assert.DoesNotContain(
            state.Constraints, value => value.Contains("Сделай красиво", StringComparison.Ordinal));
        Assert.All(state.Constraints, value =>
            Assert.True(value.Length <= AgentWorkingStateUpdater.MaxConstraintLength + 1));
    }

    [Fact]
    public void UpdateFromUserText_DedupesConstraintsAndKeepsNewOnesAtTheEnd()
    {
        var state = new WorkingState();
        state.Constraints.Add("не удалять листы");

        AgentWorkingStateUpdater.UpdateFromUserText(
            state,
            "Не удалять листы. Только планы этажей.");

        Assert.Equal(2, state.Constraints.Count);
        Assert.Equal("не удалять листы", state.Constraints[0]);
        Assert.Equal("Только планы этажей", state.Constraints[1]);
    }

    [Fact]
    public void UpdateFromUserText_KeepsAtMostTenConstraints()
    {
        var state = new WorkingState();
        var lines = Enumerable.Range(0, 15)
            .Select(index => "только шаг " + index);
        AgentWorkingStateUpdater.UpdateFromUserText(state, string.Join("\n", lines));

        Assert.Equal(AgentWorkingStateUpdater.MaxConstraints, state.Constraints.Count);
    }

    [Fact]
    public void UpdateFromProjectContext_CopiesDocumentAndView()
    {
        var state = new WorkingState();
        var before = DateTime.UtcNow;

        AgentWorkingStateUpdater.UpdateFromProjectContext(state, new AgentProjectContext
        {
            DocumentId = "doc-7",
            Title = "Project",
            PathName = @"C:\models\p.rvt",
            RevitVersion = "2026",
            ActiveViewId = 77,
            ActiveViewName = "Section A"
        });

        Assert.Equal("doc-7", state.DocumentId);
        Assert.Equal(@"C:\models\p.rvt", state.DocumentPath);
        Assert.Equal("2026", state.RevitVersion);
        Assert.Equal(77, state.ActiveViewId);
        Assert.Equal("Section A", state.ActiveViewName);
        Assert.True(state.UpdatedAt >= before);
    }

    [Fact]
    public void ObserveToolResult_ReadsChangeSetPlanDigestAndTouchedIds()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "rename_views",
            ToolResult.Ok(Payload(new
            {
                changeSetId = "cs-1",
                planDigest = "digest-1",
                changed = new object[]
                {
                    new { id = 10 },
                    new { elementId = 20 },
                    new { name = "no id" }
                }
            })));

        Assert.Equal(new[] { "cs-1" }, state.ChangeSetIds);
        Assert.Equal("digest-1", state.PlanId);
        Assert.Equal(new[] { "10", "20" }, state.TouchedElementIds);
    }

    [Fact]
    public void ObserveToolResult_KeepsNewestIdsFirstAndDedupes()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "rename_views",
            ToolResult.Ok(Payload(new { changeSetId = "cs-1", changed = new[] { new { id = 1 } } })));
        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "rename_views",
            ToolResult.Ok(Payload(new
            {
                changeSetId = "cs-2",
                changed = new object[] { new { id = 1 }, new { id = 2 } }
            })));

        Assert.Equal(new[] { "cs-2", "cs-1" }, state.ChangeSetIds);
        Assert.Equal(new[] { "2", "1" }, state.TouchedElementIds);
    }

    [Fact]
    public void ObserveToolResult_ExecutePlan_UpdatesCompletedAndPendingSteps()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "execute_plan",
            ToolResult.Ok(Payload(new
            {
                planDigest = "digest-1",
                steps = new object[]
                {
                    new { toolName = "rename_views", changed = 3, failed = 0 },
                    new { toolName = "set_parameters", changed = 0, failed = 2 }
                }
            })));

        Assert.Equal(new[] { "rename_views: 3 changed" }, state.CompletedSteps);
        Assert.Equal(new[] { "set_parameters: failed" }, state.PendingSteps);
        Assert.Equal("digest-1", state.PlanId);
    }

    [Fact]
    public void ObserveToolResult_ConfirmationRejected_AddsQuestionAndSuccessRemovesIt()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "rename_views",
            ToolResult.Fail("confirmation_rejected", "The user rejected the operation."));

        Assert.Equal(new[] { "user rejected rename_views" }, state.OpenQuestions);

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "rename_views",
            ToolResult.Ok(null, "applied"));

        Assert.Empty(state.OpenQuestions);
    }

    [Fact]
    public void ObserveToolResult_ConfirmationRejected_KeepsAtMostTenQuestions()
    {
        var state = new WorkingState();

        for (var index = 0; index < 12; index++)
        {
            AgentWorkingStateUpdater.ObserveToolResult(
                state,
                "tool_" + index,
                ToolResult.Fail("confirmation_rejected", "rejected"));
        }

        Assert.Equal(AgentWorkingStateUpdater.MaxOpenQuestions, state.OpenQuestions.Count);
        Assert.DoesNotContain("user rejected tool_0", state.OpenQuestions);
        Assert.Contains("user rejected tool_11", state.OpenQuestions);
    }

    [Fact]
    public void ObserveToolResult_MalformedData_DoesNotThrowAndUpdatesTimestamp()
    {
        var state = new WorkingState();
        var before = DateTime.UtcNow;

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "rename_views",
            ToolResult.Ok(JsonSerializer.SerializeToElement("not an object")));
        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "rename_views",
            new ToolResult { Success = true, Data = null });

        Assert.Empty(state.ChangeSetIds);
        Assert.Empty(state.TouchedElementIds);
        Assert.True(state.UpdatedAt >= before);
    }

    [Fact]
    public void ObserveToolResult_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AgentWorkingStateUpdater.ObserveToolResult(null!, "tool", ToolResult.Ok()));
        Assert.Throws<ArgumentNullException>(() =>
            AgentWorkingStateUpdater.ObserveToolResult(new WorkingState(), "tool", null!));
    }

    [Fact]
    public void ObserveToolResult_GetSelectedElements_ReplacesSelectionFromUniqueIdsAndIds()
    {
        var state = new WorkingState();
        state.SelectedUniqueIds.Add("stale");

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "get_selected_elements",
            ToolResult.Ok(Payload(new
            {
                total = 4,
                elements = new object[]
                {
                    new { uniqueId = "uid-1" },
                    new { elementId = 42 },
                    new { id = 7 },
                    new { uniqueId = "uid-1" },
                    new { uniqueId = "  " }
                }
            })));

        Assert.Equal(new[] { "uid-1", "42", "7" }, state.SelectedUniqueIds);
    }

    [Fact]
    public void ObserveToolResult_GetSelectedElements_EmptySelectionClearsPreviousIds()
    {
        var state = new WorkingState();
        state.SelectedUniqueIds.Add("stale");

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "get_selected_elements",
            ToolResult.Ok(Payload(new { total = 0, elements = Array.Empty<object>() })));

        Assert.Empty(state.SelectedUniqueIds);
    }

    [Fact]
    public void ObserveToolResult_GetSelectedElements_FailedResultKeepsSelection()
    {
        var state = new WorkingState();
        state.SelectedUniqueIds.Add("kept");

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "get_selected_elements",
            ToolResult.Fail("no_active_ui_document", "unavailable"));

        Assert.Equal(new[] { "kept" }, state.SelectedUniqueIds);
    }

    [Fact]
    public void ObserveToolResult_GetSelectedElements_CapsSelectionAtFifty()
    {
        var state = new WorkingState();
        var elements = Enumerable.Range(0, 60)
            .Select(index => (object)new { uniqueId = "uid-" + index })
            .ToArray();

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "get_selected_elements",
            ToolResult.Ok(Payload(new { total = elements.Length, elements })));

        Assert.Equal(AgentWorkingStateUpdater.MaxSelectedUniqueIds, state.SelectedUniqueIds.Count);
        Assert.Equal("uid-0", state.SelectedUniqueIds[0]);
        Assert.Equal("uid-49", state.SelectedUniqueIds[49]);
    }

    [Fact]
    public void ObserveToolResult_DryRunChangedIds_AreNotTouched()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "rename_views",
            ToolResult.Ok(Payload(new
            {
                dryRun = true,
                changeSetId = "cs-preview",
                planDigest = "digest-preview",
                changed = new[] { new { id = 10 }, new { id = 11 } }
            })));

        Assert.Empty(state.TouchedElementIds);
        Assert.Equal(new[] { "cs-preview" }, state.ChangeSetIds);
        Assert.Equal("digest-preview", state.PlanId);
    }

    [Fact]
    public void ObserveToolResult_AppliedChangedIds_AreTouched()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "rename_views",
            ToolResult.Ok(Payload(new
            {
                dryRun = false,
                changed = new[] { new { id = 10 } }
            })));

        Assert.Equal(new[] { "10" }, state.TouchedElementIds);
    }

    [Fact]
    public void ObserveToolResult_PreviewPlanSteps_DoNotReachCompletedOrPending()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "execute_plan",
            ToolResult.Ok(Payload(new
            {
                dryRun = true,
                planDigest = "digest-1",
                steps = new object[]
                {
                    new { toolName = "rename_views", changed = 3, failed = 0 },
                    new { toolName = "set_parameters", changed = 0, failed = 2 }
                }
            })));

        Assert.Empty(state.CompletedSteps);
        Assert.Empty(state.PendingSteps);
        Assert.Equal("digest-1", state.PlanId);
    }

    [Fact]
    public void ObserveToolResult_AppliedPlanSteps_ReachCompletedAndPending()
    {
        var state = new WorkingState();

        AgentWorkingStateUpdater.ObserveToolResult(
            state,
            "execute_plan",
            ToolResult.Ok(Payload(new
            {
                dryRun = false,
                steps = new object[]
                {
                    new { toolName = "rename_views", changed = 3, failed = 0 },
                    new { toolName = "set_parameters", changed = 0, failed = 2 }
                }
            })));

        Assert.Equal(new[] { "rename_views: 3 changed" }, state.CompletedSteps);
        Assert.Equal(new[] { "set_parameters: failed" }, state.PendingSteps);
    }

    private static JsonElement Payload(object value) =>
        JsonSerializer.SerializeToElement(value);
}
