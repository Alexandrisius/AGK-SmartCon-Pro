using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Plans;

public sealed partial class AgentPlanExecutorTests
{
    private const string WriteArguments = @"{""items"":[{""viewId"":1}]}";

    private static AgentPlanStep WriteStep(
        string id = "s1",
        int expectedChanges = 1,
        string? readBackTool = "read_tool") =>
        new()
        {
            StepId = id,
            ToolName = "write_tool",
            ArgumentsJson = WriteArguments,
            ExpectedChanges = expectedChanges,
            ReadBackTool = readBackTool,
            ArgumentsReadBackJson = @"{""viewId"":1}",
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation { Path = "name", Expected = "value" }
            }
        };

    private static AgentPlan Plan(params AgentPlanStep[] steps) =>
        new() { Goal = "test plan", Steps = steps };

    private static AgentPlanExecutionContext Context(
        AgentApprovalMode mode = AgentApprovalMode.ConfirmWrites) =>
        new() { DocumentId = "doc", ApprovalMode = mode };

    private static AgentPlanExecutor CreateExecutor(
        ScriptedTool writeTool,
        ScriptedTool readTool,
        RecordingConfirmation confirmation,
        RecordingAuditLog audit,
        TimeSpan? lifetime = null)
    {
        var registry = new AgentToolRegistry(new IAgentTool[] { writeTool, readTool });
        return new AgentPlanExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            confirmation,
            audit,
            lifetime ?? TimeSpan.FromMinutes(5));
    }

    private static ScriptedTool ReadTool() =>
        new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new { name = "value" }),
                "read"),
            schema: @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""}},""additionalProperties"":false}");

    [Fact]
    public async Task ApprovedPlan_RunsDryRunAppliesAndVerifies()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = ReadTool();
        var confirmation = new RecordingConfirmation();
        var audit = new RecordingAuditLog();
        var executor = CreateExecutor(writeTool, readTool, confirmation, audit);

        var result = await executor.ExecuteAsync(
            Plan(WriteStep()), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Approved);
        Assert.Equal(2, writeTool.Executions.Count);
        Assert.Contains("\"dryRun\":true", writeTool.Executions[0].ArgumentsJson);
        Assert.Contains("\"dryRun\":false", writeTool.Executions[1].ArgumentsJson);
        Assert.True(writeTool.Executions[1].Approved);

        var request = Assert.Single(confirmation.Requests);
        Assert.False(string.IsNullOrWhiteSpace(request.Nonce));
        Assert.NotNull(request.ExpiresAt);
        Assert.False(string.IsNullOrWhiteSpace(request.PlanDigest));
        Assert.Equal(result.PlanDigest, request.PlanDigest);

        Assert.Contains(result.Verified, line => line.Contains("name = value"));
        Assert.Empty(result.Mismatches);
        Assert.Contains(audit.Entries, entry => entry.Tool == "plan#validate" && entry.Result == "valid");
        Assert.Contains(audit.Entries, entry => entry.Tool == "plan#apply" && entry.Result == "ok");
    }

    [Fact]
    public async Task FidelityMismatch_StopsBeforeApproval()
    {
        var writeTool = new ScriptedTool(
            "write_tool",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = 1 }, new { id = 2 } },
                    affected = 2
                }),
                "two changes"));
        var readTool = ReadTool();
        var confirmation = new RecordingConfirmation();
        var audit = new RecordingAuditLog();
        var executor = CreateExecutor(writeTool, readTool, confirmation, audit);

        var result = await executor.ExecuteAsync(
            Plan(WriteStep(expectedChanges: 1)),
            Context(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_fidelity_mismatch", result.ErrorCode);
        Assert.Single(writeTool.Executions);
        Assert.Empty(confirmation.Requests);
        Assert.Contains(audit.Entries, entry => entry.Tool == "plan#fidelity" &&
                                                entry.Result == "mismatch");

        // The failing preview must still return the per-step diff (stress #2).
        var step = Assert.Single(result.Steps);
        Assert.True(step.DryRunOk);
        Assert.Equal(2, step.Changed);
        Assert.Contains("steps[]", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedApproval_NeverApplies()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = ReadTool();
        var confirmation = new RecordingConfirmation
        {
            Result = AgentConfirmationResult.Rejected
        };
        var executor = CreateExecutor(
            writeTool, readTool, confirmation, new RecordingAuditLog());

        var result = await executor.ExecuteAsync(
            Plan(WriteStep()), Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_rejected", result.ErrorCode);
        Assert.Single(writeTool.Executions);
    }

    [Fact]
    public async Task PlanMutationAfterApproval_IsRejectedByDigest()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = ReadTool();
        var steps = new List<AgentPlanStep> { WriteStep() };
        var plan = new AgentPlan { Goal = "goal", Steps = steps };
        var confirmation = new RecordingConfirmation
        {
            OnRequest = _ =>
            {
                steps.Add(WriteStep("s2"));
                return Task.CompletedTask;
            }
        };
        var executor = CreateExecutor(
            writeTool, readTool, confirmation, new RecordingAuditLog());

        var result = await executor.ExecuteAsync(plan, Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("confirmation_digest_mismatch", result.ErrorCode);
        Assert.Single(writeTool.Executions);
    }

    [Fact]
    public async Task ExpiredApproval_IsRejected()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = ReadTool();
        var confirmation = new RecordingConfirmation
        {
            OnRequest = async _ => await Task.Delay(120)
        };
        var executor = CreateExecutor(
            writeTool,
            readTool,
            confirmation,
            new RecordingAuditLog(),
            TimeSpan.FromMilliseconds(30));

        var result = await executor.ExecuteAsync(
            Plan(WriteStep()), Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("confirmation_expired", result.ErrorCode);
        Assert.Single(writeTool.Executions);
    }

    [Fact]
    public async Task ReadOnlyMode_BlocksPlan()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = ReadTool();
        var executor = CreateExecutor(
            writeTool, readTool, new RecordingConfirmation(), new RecordingAuditLog());

        var result = await executor.ExecuteAsync(
            Plan(WriteStep()),
            Context(AgentApprovalMode.ReadOnly),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("read_only_mode", result.ErrorCode);
        Assert.Empty(writeTool.Executions);
    }

    [Fact]
    public async Task PreflightFailure_StopsBeforeDryRun()
    {
        var writeTool = new ScriptedTool("write_tool")
        {
            PreflightResult = PlanPreflightResult.Fail("unresolved_id", "View 1 does not exist.")
        };
        var readTool = ReadTool();
        var executor = CreateExecutor(
            writeTool, readTool, new RecordingConfirmation(), new RecordingAuditLog());

        var result = await executor.ExecuteAsync(
            Plan(WriteStep()), Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unresolved_id", result.ErrorCode);
        Assert.Empty(writeTool.Executions);
    }

    [Fact]
    public async Task ReadBackMismatch_IsReportedWithoutFailingApply()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new { name = "other" }),
                "read"),
            schema: @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""}},""additionalProperties"":false}");
        var executor = CreateExecutor(
            writeTool, readTool, new RecordingConfirmation(), new RecordingAuditLog());

        var result = await executor.ExecuteAsync(
            Plan(WriteStep()), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Mismatches);
        Assert.Contains(result.Mismatches, line => line.Contains("name"));
    }

    [Fact]
    public async Task InvalidPlan_DoesNotTouchTools()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = ReadTool();
        var executor = CreateExecutor(
            writeTool, readTool, new RecordingConfirmation(), new RecordingAuditLog());
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "missing_tool",
            ArgumentsJson = "{}"
        };

        var result = await executor.ExecuteAsync(
            new AgentPlan { Goal = "goal", Steps = new[] { step } },
            Context(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_invalid", result.ErrorCode);
        Assert.Empty(writeTool.Executions);
    }

    [Fact]
    public async Task AutoApproveSession_SkipsConfirmationAndApplies()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = ReadTool();
        var confirmation = new RecordingConfirmation
        {
            Result = AgentConfirmationResult.Rejected
        };
        var audit = new RecordingAuditLog();
        var executor = CreateExecutor(writeTool, readTool, confirmation, audit);

        var result = await executor.ExecuteAsync(
            Plan(WriteStep()),
            Context(AgentApprovalMode.AutoApproveSession),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(confirmation.Requests);
        Assert.Equal(2, writeTool.Executions.Count);
        Assert.Contains(audit.Entries, entry =>
            entry.Tool == "plan#approval" && entry.Result == "auto_approved");
    }

    [Fact]
    public async Task AutoApproveSession_PlanWithDiscard_StillAsksConfirmation()
    {
        var discard = new ScriptedTool(
            "discard_change_set", AgentToolRisk.Write, supportsDryRun: false);
        var readTool = ReadTool();
        var confirmation = new RecordingConfirmation();
        var registry = new AgentToolRegistry(new IAgentTool[] { discard, readTool });
        var executor = new AgentPlanExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            confirmation,
            new RecordingAuditLog());
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "discard_change_set",
            ArgumentsJson = "{}"
        };

        var result = await executor.ExecuteAsync(
            Plan(step),
            Context(AgentApprovalMode.AutoApproveSession),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(confirmation.Requests);
        Assert.Single(discard.Executions);
    }

    [Fact]
    public async Task DryRunFailure_StopsBeforeApproval()
    {
        var writeTool = new ScriptedTool(
            "write_tool",
            handler: _ => ToolResult.Fail("boom", "dry run exploded"));
        var readTool = ReadTool();
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            writeTool, readTool, confirmation, new RecordingAuditLog());

        var result = await executor.ExecuteAsync(
            Plan(WriteStep()), Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_dry_run_failed", result.ErrorCode);
        Assert.Single(writeTool.Executions);
        Assert.Empty(confirmation.Requests);
    }

    [Fact]
    public async Task FailedReadStep_StopsBeforeWriteApply()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Fail("read_boom", "read failed"),
            schema: @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""}},""additionalProperties"":false}");
        var executor = CreateExecutor(
            writeTool, readTool, new RecordingConfirmation(), new RecordingAuditLog());
        var readStep = new AgentPlanStep
        {
            StepId = "read",
            ToolName = "read_tool",
            ArgumentsJson = @"{""viewId"":1}"
        };
        var writeStep = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = WriteArguments,
            ExpectedChanges = 1,
            DependsOn = new[] { "read" }
        };

        var result = await executor.ExecuteAsync(
            new AgentPlan { Goal = "goal", Steps = new[] { readStep, writeStep } },
            Context(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_read_failed", result.ErrorCode);
        Assert.Single(writeTool.Executions);
        Assert.All(writeTool.Executions,
            execution => Assert.Contains("\"dryRun\":true", execution.ArgumentsJson));
    }

    [Fact]
    public async Task Preview_DependentStep_SkipReasonNamesBlockingStep()
    {
        var writeTool = new ScriptedTool("write_tool");
        var executor = CreateExecutor(writeTool, ReadTool(), new RecordingConfirmation(), new RecordingAuditLog());

        var steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "s1",
                ToolName = "write_tool",
                ArgumentsJson = WriteArguments,
                ExpectedChanges = 1
            },
            new AgentPlanStep
            {
                StepId = "s2",
                ToolName = "write_tool",
                ArgumentsJson = WriteArguments,
                ExpectedChanges = 1,
                DependsOn = new[] { "s1" }
            }
        };

        var result = await executor.PreviewAsync(Plan(steps), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.PreviewIncomplete);
        Assert.Contains("s2", result.NotPreviewed);
        var skipped = result.Steps.Single(step => step.StepId == "s2");
        Assert.True(skipped.PreviewSkipped);
        Assert.Equal("depends_on_unapplied_step:s1(write_tool)", skipped.PreviewSkipReason);
    }

    [Fact]
    public async Task Apply_FailedStep_MarksDependentsUpstreamFailed()
    {
        var writeTool = new ScriptedTool(
            "write_tool",
            handler: args => args.ValueKind == JsonValueKind.Object
                              && args.TryGetProperty("dryRun", out var dryRun)
                              && dryRun.ValueKind == JsonValueKind.False
                ? ToolResult.Fail("boom", "apply exploded")
                : ToolResult.Ok(
                    JsonSerializer.SerializeToElement(new
                    {
                        changed = new[] { new { id = 1 } },
                        skipped = Array.Empty<object>(),
                        failed = Array.Empty<object>(),
                        total = 1,
                        affected = 1
                    }),
                    "preview ok"));
        var executor = CreateExecutor(writeTool, ReadTool(), new RecordingConfirmation(), new RecordingAuditLog());

        var steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "s1",
                ToolName = "write_tool",
                ArgumentsJson = WriteArguments,
                ExpectedChanges = 1
            },
            new AgentPlanStep
            {
                StepId = "s2",
                ToolName = "write_tool",
                ArgumentsJson = WriteArguments,
                DependsOn = new[] { "s1" }
            },
            new AgentPlanStep
            {
                StepId = "s3",
                ToolName = "read_tool",
                ArgumentsJson = @"{""viewId"":1}"
            }
        };

        var result = await executor.ExecuteAsync(Plan(steps), Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_apply_failed", result.ErrorCode);
        var dependent = result.Steps.Single(step => step.StepId == "s2");
        Assert.NotNull(dependent.ApplyResult);
        Assert.False(dependent.ApplyResult!.Success);
        Assert.Equal("upstream_step_failed", dependent.ApplyResult.ErrorCode);
        Assert.Contains("s1", dependent.ApplyResult.Message, StringComparison.Ordinal);
        var independent = result.Steps.Single(step => step.StepId == "s3");
        Assert.Null(independent.ApplyResult);
    }

    [Fact]
    public async Task ExpectedChangesAtLeast_AcceptsMoreChanges()
    {
        var writeTool = new ScriptedTool(
            "write_tool",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = 1 }, new { id = 2 } },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    total = 2,
                    affected = 2
                }),
                "two changes"));
        var executor = CreateExecutor(
            writeTool,
            ReadTool(),
            new RecordingConfirmation(),
            new RecordingAuditLog());

        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = WriteArguments,
            ExpectedChangesAtLeast = 2
        };

        var result = await executor.ExecuteAsync(
            Plan(step), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Steps[0].FidelityMatched);
        Assert.Equal(2, result.Steps[0].FidelityExpected);
    }

    [Fact]
    public async Task ExpectedChanges_AllNoOpStep_IsAcceptedWithNote()
    {
        // Benchmark run 2, task 1: values were already correct, the step was skipped,
        // and the strict expectedChanges check forced the model to rewrite expectations.
        var writeTool = new ScriptedTool(
            "write_tool",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = Array.Empty<object>(),
                    skipped = new[] { new { id = 1 } },
                    failed = Array.Empty<object>(),
                    total = 1,
                    affected = 0
                }),
                "nothing to do"));
        var executor = CreateExecutor(
            writeTool,
            ReadTool(),
            new RecordingConfirmation(),
            new RecordingAuditLog());

        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = WriteArguments,
            ExpectedChanges = 1
        };

        var result = await executor.ExecuteAsync(
            Plan(step), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Steps[0].FidelityMatched);
        Assert.NotNull(result.Steps[0].FidelityNote);
        Assert.Contains("no-op", result.Steps[0].FidelityNote!, StringComparison.Ordinal);
    }
}
