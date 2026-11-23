using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Plans;

public sealed class AgentPlanExecutorTests
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
    public void ComputeDigest_IsWhitespaceAndOrderInsensitive_ButDocumentBound()
    {
        var stepA = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}]}",
            DependsOn = new[] { "b", "a" },
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation { Path = "name", Expected = "x" },
                new AgentPlanExpectation { Path = "id", Expected = "1" }
            }
        };
        var stepB = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = "{ \"items\" : [ { \"viewId\" : 1 } ] }",
            DependsOn = new[] { "a", "b" },
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation { Path = "id", Expected = "1" },
                new AgentPlanExpectation { Path = "name", Expected = "x" }
            }
        };

        var digestA = AgentPlanExecutor.ComputeDigest(Plan(stepA), "doc-1");
        var digestB = AgentPlanExecutor.ComputeDigest(Plan(stepB), "doc-1");
        var digestOtherDocument = AgentPlanExecutor.ComputeDigest(Plan(stepA), "doc-2");

        Assert.Equal(digestA, digestB);
        Assert.NotEqual(digestA, digestOtherDocument);
    }

    [Fact]
    public void ComputeDigest_IgnoresDryRunFlags()
    {
        var stepA = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}],""dryRun"":true}",
            DryRun = true
        };
        var stepB = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}],""dryRun"":false}",
            DryRun = false
        };

        var digestA = AgentPlanExecutor.ComputeDigest(Plan(stepA), "doc-1");
        var digestB = AgentPlanExecutor.ComputeDigest(Plan(stepB), "doc-1");

        Assert.Equal(digestA, digestB);
    }

    [Fact]
    public void ComputeDigest_IgnoresReadBackFields()
    {
        var withoutReadBack = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}]}"
        };
        var withReadBack = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}]}",
            ReadBackTool = "read_tool",
            ArgumentsReadBackJson = @"{""viewId"":1}",
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation { Path = "name", Expected = "x" }
            }
        };

        Assert.Equal(
            AgentPlanExecutor.ComputeDigest(Plan(withoutReadBack), "doc-1"),
            AgentPlanExecutor.ComputeDigest(Plan(withReadBack), "doc-1"));
    }

    [Fact]
    public async Task ReadBack_ByElementId_ResolvesTheTargetCard()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    views = new[]
                    {
                        new { viewId = 1, name = "reference", templateName = (string?)null },
                        new { viewId = 2, name = "target", templateName = (string?)"T" }
                    }
                }),
                "read"),
            schema: @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""}},""additionalProperties"":false}");
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            writeTool, readTool, confirmation, new RecordingAuditLog());
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = WriteArguments,
            ExpectedChanges = 1,
            ReadBackTool = "read_tool",
            ArgumentsReadBackJson = "{}",
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation { Path = "byId[2].templateName", Expected = "T" }
            }
        };

        var result = await executor.ExecuteAsync(
            Plan(step), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Equal(1, result.ExpectationsChecked);
        Assert.Contains(result.Verified, line => line.Contains("byId[2].templateName"));
    }

    [Fact]
    public async Task ReadBack_ByName_ResolvesFlatFamilyParameterArray()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    parameters = new object[]
                    {
                        new
                        {
                            id = "-1010101",
                            name = "Ширина",
                            scope = "type",
                            valuesByType = new object[]
                            {
                                new { typeName = "Тип 1", displayValue = "1200 мм" }
                            }
                        },
                        new
                        {
                            id = "-1010102",
                            name = "Высота",
                            scope = "instance",
                            valuesByType = Array.Empty<object>()
                        }
                    }
                }),
                "read"),
            schema: @"{""type"":""object"",""properties"":{},""additionalProperties"":false}");
        var executor = CreateExecutor(
            writeTool, readTool, new RecordingConfirmation(), new RecordingAuditLog());
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = WriteArguments,
            ExpectedChanges = 1,
            ReadBackTool = "read_tool",
            ArgumentsReadBackJson = "{}",
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation
                {
                    Path = "parameters.byName[Ширина].valuesByType.byName[Тип 1].displayValue",
                    Expected = "1200 мм"
                },
                new AgentPlanExpectation
                {
                    Path = "parameters.byId[-1010101].name",
                    Expected = "Ширина"
                }
            }
        };

        var result = await executor.ExecuteAsync(
            Plan(step), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Equal(2, result.ExpectationsChecked);
    }

    [Fact]
    public async Task ReadBack_BracketIndexPath_ResolvesTheSameAsDotIndex()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    categories = new[]
                    {
                        new { categoryId = 1, name = "Walls" },
                        new { categoryId = 2, name = "Pipes" }
                    }
                }),
                "read"),
            schema: @"{""type"":""object"",""properties"":{},""additionalProperties"":false}");
        var executor = CreateExecutor(
            writeTool, readTool, new RecordingConfirmation(), new RecordingAuditLog());
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = WriteArguments,
            ExpectedChanges = 1,
            ReadBackTool = "read_tool",
            ArgumentsReadBackJson = "{}",
            ReadBackExpectations = new[]
            {
                // The bracket form is what tool output shows; "filter.categories[0].name"
                // used to report mismatch even though the apply succeeded (B4).
                new AgentPlanExpectation { Path = "categories[0].name", Expected = "Walls" },
                new AgentPlanExpectation { Path = "categories.1.name", Expected = "Pipes" }
            }
        };

        var result = await executor.ExecuteAsync(
            Plan(step), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Equal(2, result.ExpectationsChecked);
    }

    [Fact]
    public async Task FamilyIdAlias_ReTargetsLaterApplySteps()
    {
        const string schema =
            @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""}," +
            @"""dryRun"":{""type"":""boolean""}},""additionalProperties"":false}";
        var writeTool = new ScriptedTool(
            "family_write_tool",
            handler: args =>
            {
                var dryRun = args.TryGetProperty("dryRun", out var flag)
                             && flag.ValueKind == JsonValueKind.True;
                var payload = dryRun
                    ? JsonSerializer.SerializeToElement(new
                    {
                        dryRun = true,
                        changed = new[] { new { id = 1 } },
                        skipped = Array.Empty<object>(),
                        failed = Array.Empty<object>(),
                        total = 1,
                        affected = 1
                    })
                    : JsonSerializer.SerializeToElement(new
                    {
                        dryRun = false,
                        familyId = 2,
                        previousFamilyId = 1,
                        changed = new[] { new { id = 1 } },
                        skipped = Array.Empty<object>(),
                        failed = Array.Empty<object>(),
                        total = 1,
                        affected = 1
                    });
                return ToolResult.Ok(payload, "ok", 1);
            },
            schema: schema);
        var executor = CreateExecutor(
            writeTool,
            ReadTool(),
            new RecordingConfirmation(),
            new RecordingAuditLog());

        var steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "s1",
                ToolName = "family_write_tool",
                ArgumentsJson = @"{""familyId"":1}",
                ExpectedChanges = 1
            },
            new AgentPlanStep
            {
                StepId = "s2",
                ToolName = "family_write_tool",
                ArgumentsJson = @"{""familyId"":1}",
                ExpectedChanges = 1,
                DependsOn = new[] { "s1" }
            }
        };

        var result = await executor.ExecuteAsync(
            Plan(steps), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.PreviewIncomplete);
        Assert.Contains("s2", result.NotPreviewed);
        Assert.Equal(3, writeTool.Executions.Count);
        Assert.Contains("\"familyId\":2", writeTool.Executions[^1].ArgumentsJson);
    }

    [Fact]
    public async Task PlanStepConflict_TemplateControlsCrop()
    {
        var templateTool = new ScriptedTool(
            "apply_view_template",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = 1 } },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    total = 1,
                    affected = 1,
                    delta = new[]
                    {
                        new
                        {
                            viewId = 1,
                            templateId = 9,
                            templateName = "T",
                            controls = new { crop = true, visibility = true, viewRange = true }
                        }
                    }
                }),
                "template"));
        var cropTool = new ScriptedTool("set_view_crop");
        var registry = new AgentToolRegistry(
            new IAgentTool[] { templateTool, cropTool, ReadTool() });
        var executor = new AgentPlanExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new RecordingConfirmation(),
            new RecordingAuditLog());

        var steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "tpl",
                ToolName = "apply_view_template",
                ArgumentsJson = @"{""items"":[{""viewId"":1,""templateId"":9}]}",
                ExpectedChanges = 1
            },
            new AgentPlanStep
            {
                StepId = "crop",
                ToolName = "set_view_crop",
                ArgumentsJson = @"{""items"":[{""viewId"":1,""crop"":{}}]}",
                ExpectedChanges = 1,
                DependsOn = new[] { "tpl" }
            }
        };

        var result = await executor.ExecuteAsync(
            Plan(steps), Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_step_conflict", result.ErrorCode);
        Assert.Contains("crop", result.Message, StringComparison.Ordinal);
        Assert.Contains("tpl", result.Message, StringComparison.Ordinal);
    }

    private static ScriptedTool TemplateTool(string deltaItem) =>
        new(
            "apply_view_template",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = 1 } },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    total = 1,
                    affected = 1,
                    delta = new[] { JsonSerializer.Deserialize<object>(deltaItem) }
                }),
                "template"));

    private static ScriptedTool FilterTool() =>
        new(
            "create_view_filter",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = 1 } },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    total = 1,
                    affected = 1
                }),
                "filter"),
            schema:
            @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array"",""items"":{""type"":""integer""}}," +
            @"""visibility"":{""type"":""string""},""dryRun"":{""type"":""boolean""}},""additionalProperties"":false}");

    private static ScriptedTool InclusionTool() =>
        new(
            "set_view_template_inclusion",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = 1 } },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    total = 1,
                    affected = 1,
                    templateId = 9,
                    parameters = new[]
                    {
                        new { section = "filters", included = false }
                    }
                }),
                "inclusion"),
            schema:
            @"{""type"":""object"",""properties"":{""templateId"":{""type"":""integer""}," +
            @"""parameters"":{""type"":""array"",""items"":{""type"":""string""}}," +
            @"""included"":{""type"":""boolean""},""dryRun"":{""type"":""boolean""}}," +
            @"""additionalProperties"":false}");

    private static AgentPlanExecutor FilterPlanExecutor(params IAgentTool[] extraTools)
    {
        var tools = new List<IAgentTool> { FilterTool() };
        tools.AddRange(extraTools);
        tools.Add(ReadTool());
        return new AgentPlanExecutor(
            new AgentToolRegistry(tools),
            new ImmediateAwaitableEvent(),
            new RecordingConfirmation(),
            new RecordingAuditLog());
    }

    private static AgentPlanStep TemplateStep(string id = "tpl") => new()
    {
        StepId = id,
        ToolName = "apply_view_template",
        ArgumentsJson = @"{""items"":[{""viewId"":1,""templateId"":9}]}",
        ExpectedChanges = 1
    };

    private static AgentPlanStep FilterStep(string id = "flt", string? dependsOn = "tpl") => new()
    {
        StepId = id,
        ToolName = "create_view_filter",
        ArgumentsJson =
            @"{""viewIds"":[1],""visibility"":""hide"",""dryRun"":false}",
        ExpectedChanges = 1,
        DependsOn = dependsOn is null ? Array.Empty<string>() : new[] { dependsOn }
    };

    [Fact]
    public async Task PlanStepConflict_TemplateControlsOnlyVisibility_AllowsFilterStep()
    {
        // F1 × WA: a template that controls model/annotation graphics but NOT
        // "V/G Overrides Filters" does not stop create_view_filter — the tool's
        // `visibility` argument is the filter's own state, not a
        // VIS_GRAPHICS_MODEL write. The detector used to reject this plan.
        var executor = FilterPlanExecutor(
            TemplateTool(
                """
                {"viewId":1,"templateId":9,"templateName":"T","templateControlsFilters":false,
                 "controls":{"crop":false,"visibility":true,"viewRange":false}}
                """));

        var result = await executor.ExecuteAsync(
            Plan(TemplateStep(), FilterStep()), Context(), CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task PlanStepConflict_TemplateControlsFilters_RejectsFilterStep_WithEscape()
    {
        // The other side: a template that includes "V/G Overrides Filters"
        // really does take over the filter list — create_view_filter on the
        // view would fail with view_controlled_by_template. The conflict
        // message must name the two workable paths.
        var executor = FilterPlanExecutor(
            TemplateTool(
                """
                {"viewId":1,"templateId":9,"templateName":"T","templateControlsFilters":true,
                 "controls":{"crop":false,"visibility":false,"viewRange":false}}
                """));

        var result = await executor.ExecuteAsync(
            Plan(TemplateStep(), FilterStep()), Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_step_conflict", result.ErrorCode);
        Assert.Contains("filters", result.Message, StringComparison.Ordinal);
        Assert.Contains(
            "set_view_template_inclusion(templateId=9, parameters=[\"filters\"], included=false)",
            result.Message, StringComparison.Ordinal);
        Assert.Contains("viewIds=[9]", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanStepConflict_DetachedFilters_TemplateThenFilterRuns()
    {
        // The advised escape, executed: detach 'filters' from the template
        // first, then apply it, then create the per-view filter — the plan
        // must pass end to end.
        var executor = FilterPlanExecutor(
            TemplateTool(
                """
                {"viewId":1,"templateId":9,"templateName":"T","templateControlsFilters":false,
                 "controls":{"crop":false,"visibility":true,"viewRange":false}}
                """),
            InclusionTool());

        var result = await executor.ExecuteAsync(
            Plan(
                new AgentPlanStep
                {
                    StepId = "detach",
                    ToolName = "set_view_template_inclusion",
                    ArgumentsJson =
                        @"{""templateId"":9,""parameters"":[""filters""],""included"":false,""dryRun"":false}",
                    ExpectedChanges = 1
                },
                TemplateStep(),
                FilterStep(dependsOn: null)),
            Context(),
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
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

    [Fact]
    public void ComputeDigest_IgnoresGoalText()
    {
        // Benchmark run 2, task 5: rewording the goal between preview and apply
        // produced plan_digest_mismatch although the executable steps were identical.
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = @"{""items"":[{""viewId"":1}]}"
        };
        var first = new AgentPlan { Goal = "Переименовать виды", Steps = new[] { step } };
        var second = new AgentPlan
        {
            Goal = "Привести имена видов к схеме разделов",
            Steps = new[] { step }
        };

        Assert.Equal(
            AgentPlanExecutor.ComputeDigest(first, "doc-1"),
            AgentPlanExecutor.ComputeDigest(second, "doc-1"));
    }

    [Fact]
    public void ConfirmationLedger_ConsumesNonceOnce()
    {
        var ledger = new AgentConfirmationLedger();

        Assert.True(ledger.TryConsume("nonce"));
        Assert.False(ledger.TryConsume("nonce"));
        Assert.True(ledger.IsConsumed("nonce"));
        Assert.False(ledger.TryConsume(null));
        Assert.False(ledger.TryConsume(string.Empty));
    }
}
