using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Plans;

/// <summary>
/// Step references {"$step": …} between plan steps, the onError="continue"
/// contract and the template-parameter conflict (S.42).
/// </summary>
public sealed class AgentPlanStepReferencesTests
{
    // ── pure resolution ───────────────────────────────────────────────────

    [Fact]
    public void ContainsStepReference_MatchesOnlyTheMarker()
    {
        Assert.True(AgentPlanExecutor.ContainsStepReference(
            @"{""levels"":[{""$step"":""s1"",""path"":""created.levelId""}]}"));
        Assert.False(AgentPlanExecutor.ContainsStepReference(
            @"{""viewId"":1,""path"":""created.levelId""}"));
        Assert.False(AgentPlanExecutor.ContainsStepReference(null));
        Assert.False(AgentPlanExecutor.ContainsStepReference(string.Empty));
    }

    [Fact]
    public void ReferencedStepIds_CollectsEveryReference()
    {
        var ids = AgentPlanExecutor.ReferencedStepIds(
            @"{""a"":{""$step"":""s1"",""path"":""created.id""}," +
            @"""b"":[{""$step"":""s3"",""path"":""createdViews.0.viewId""}]}");

        Assert.Equal(new[] { "s1", "s3" }, ids);
    }

    [Fact]
    public void TryResolveStepReferences_ResolvesCreatedIdAndArrayIndexes()
    {
        var results = new Dictionary<string, AgentPlanStepResult>(StringComparer.Ordinal)
        {
            ["s1"] = new() { StepId = "s1", ToolName = "create_sheet" },
            ["s2"] = new() { StepId = "s2", ToolName = "create_viewports" }
        };
        results["s1"].ApplyResult = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new
            {
                created = new { sheetId = 4711 },
                createdViewports = new[] { new { viewId = 100, name = "v" } }
            }),
            "sheet");

        var resolved = AgentPlanExecutor.TryResolveStepReferences(
            @"{""sheetId"":{""$step"":""s1"",""path"":""created.sheetId""}," +
            @"""viewportIds"":[{""$step"":""s1"",""path"":""createdViewports.0.viewId""}]}",
            results,
            out var error);

        Assert.Null(error);
        Assert.NotNull(resolved);
        using var document = JsonDocument.Parse(resolved!);
        Assert.Equal(4711, document.RootElement.GetProperty("sheetId").GetInt64());
        Assert.Equal(100, document.RootElement.GetProperty("viewportIds")[0].GetInt64());
    }

    [Fact]
    public void TryResolveStepReferences_NamesTheMissingPiece()
    {
        var ok = new AgentPlanStepResult { StepId = "s1", ToolName = "create_sheet" };
        ok.ApplyResult = ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { created = new { sheetId = 1 } }), "ok");
        var failed = new AgentPlanStepResult { StepId = "s2", ToolName = "create_schedule" };
        failed.ApplyResult = ToolResult.Fail("schedule_failed", "boom");

        var results = new Dictionary<string, AgentPlanStepResult>(StringComparer.Ordinal)
        {
            ["s1"] = ok,
            ["s2"] = failed,
            ["s3"] = new() { StepId = "s3", ToolName = "whatever" }
        };

        Assert.True(AgentPlanExecutor.TryResolveStepReferences(
            @"{""a"":{""$step"":""s1"",""path"":""created.nothing""}}", results, out var error) is null);
        Assert.Contains("addresses nothing", error, StringComparison.Ordinal);

        Assert.True(AgentPlanExecutor.TryResolveStepReferences(
            @"{""a"":{""$step"":""s2"",""path"":""created.sheetId""}}", results, out error) is null);
        Assert.Contains("failed (schedule_failed)", error, StringComparison.Ordinal);

        Assert.True(AgentPlanExecutor.TryResolveStepReferences(
            @"{""a"":{""$step"":""s3"",""path"":""x""}}", results, out error) is null);
        Assert.Contains("no apply result", error, StringComparison.Ordinal);
    }

    // ── validator ─────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<AgentToolDefinition> Definitions =
        new[]
        {
            new ScriptedTool("create_level", schema:
                @"{""type"":""object"",""properties"":{""name"":{""type"":""string""}," +
                @"""elevationMm"":{""type"":""number""}},""additionalProperties"":false}"),
            new ScriptedTool("create_floor_plans", schema:
                @"{""type"":""object"",""properties"":{""levels"":{""type"":""array""," +
                @"""items"":{""type"":""integer""}}},""additionalProperties"":false}"),
            new ScriptedTool("write_tool", schema:
                @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""}}," +
                @"""additionalProperties"":false}")
        }.Select(tool => tool.Definition).ToList();

    [Fact]
    public void Validate_ReferenceCarryingArgsSkipSchemaCheckAndValidateTheTarget()
    {
        var plan = new AgentPlan
        {
            Goal = "new floor",
            Steps = new[]
            {
                new AgentPlanStep
                {
                    StepId = "s1",
                    ToolName = "create_level",
                    // Would break create_level's schema as a literal — a reference is not a value.
                    ArgumentsJson = @"{""name"":""X"",""elevationMm"":{""$step"":""s9"",""path"":""x""}}",
                    ExpectedChanges = 1
                }
            }
        };

        var result = AgentPlanValidator.Validate(plan, Definitions);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "plan_dependency_unknown");
        Assert.DoesNotContain(result.Errors, issue => issue.Code == "plan_args_invalid");
    }

    [Fact]
    public void Validate_KnownReferenceWithSchemaBreakingLiteral_IsAccepted()
    {
        var plan = new AgentPlan
        {
            Goal = "new floor",
            Steps = new[]
            {
                new AgentPlanStep
                {
                    StepId = "s1",
                    ToolName = "create_level",
                    ArgumentsJson = @"{""name"":""X"",""elevationMm"":2800}",
                    ExpectedChanges = 1
                },
                new AgentPlanStep
                {
                    StepId = "s2",
                    ToolName = "create_floor_plans",
                    ArgumentsJson = @"{""levels"":[{""$step"":""s1"",""path"":""created.levelId""}]}",
                    DependsOn = new[] { "s1" }
                }
            }
        };

        var result = AgentPlanValidator.Validate(plan, Definitions);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(issue => issue.Message)));
    }

    [Fact]
    public void Validate_UnknownOnErrorValue_IsRejected()
    {
        var plan = new AgentPlan
        {
            Goal = "plan",
            Steps = new[]
            {
                new AgentPlanStep
                {
                    StepId = "s1",
                    ToolName = "write_tool",
                    ArgumentsJson = @"{""viewId"":1}",
                    OnError = "retry"
                }
            }
        };

        var result = AgentPlanValidator.Validate(plan, Definitions);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "plan_on_error_invalid");
    }

    // ── executor: onError and references at apply ─────────────────────────

    private static AgentPlanExecutor Executor(params IAgentTool[] tools) =>
        new(
            new AgentToolRegistry(tools),
            new ImmediateAwaitableEvent(),
            new RecordingConfirmation(),
            new RecordingAuditLog());

    [Fact]
    public async Task OnErrorContinue_RecordsExpectedFailureAndKeepsRunning()
    {
        var failing = new ScriptedTool("negative_check", handler: _ =>
            ToolResult.Fail("crop_controlled_by_template", "the view is template-controlled"));
        var ok = new ScriptedTool("write_tool");
        var executor = Executor(failing, ok);

        var steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "s1",
                ToolName = "negative_check",
                ArgumentsJson = @"{""viewId"":1}",
                OnError = "continue"
            },
            new AgentPlanStep
            {
                StepId = "s2",
                ToolName = "write_tool",
                ArgumentsJson = @"{""viewId"":1}",
                ExpectedChanges = 1
            }
        };

        var result = await executor.ExecuteAsync(
            new AgentPlan { Goal = "guards", Steps = steps }, Context(), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.ExpectedFailureCount);
        Assert.True(result.Steps.Single(step => step.StepId == "s1").ExpectedFailure);
        Assert.NotNull(result.Steps.Single(step => step.StepId == "s2").ApplyResult);
        Assert.Equal(2, ok.Executions.Count);
    }

    [Fact]
    public async Task OnErrorContinue_DryRunFailureIsAlsoExpected()
    {
        // The negative check is a write tool: its DRY run fails too, and the
        // preview must not abort for an expected failure.
        var failing = new ScriptedTool("negative_check", handler: _ =>
            ToolResult.Fail("view_controlled_by_template", "controlled"));
        var ok = new ScriptedTool("write_tool");
        var executor = Executor(failing, ok);

        var steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "s1",
                ToolName = "negative_check",
                ArgumentsJson = @"{""viewId"":1}",
                OnError = "continue"
            },
            new AgentPlanStep
            {
                StepId = "s2",
                ToolName = "write_tool",
                ArgumentsJson = @"{""viewId"":1}",
                ExpectedChanges = 1
            }
        };

        var result = await executor.PreviewAsync(
            new AgentPlan { Goal = "guards", Steps = steps }, Context(), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.ExpectedFailureCount);
        Assert.False(
            result.Steps.Single(step => step.StepId == "s1").DryRunResult!.Success,
            "the negative check should fail its dry run");
    }

    [Fact]
    public async Task StepReference_ResolvesAtApplyFromTheRealResult()
    {
        var creator = new ScriptedTool("create_level", schema:
            @"{""type"":""object"",""properties"":{""name"":{""type"":""string""}," +
            @"""elevationMm"":{""type"":""number""}},""additionalProperties"":false}",
            handler: args =>
        {
            var dry = args.TryGetProperty("dryRun", out var flag) && flag.ValueKind == JsonValueKind.True;
            return ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = dry ? -1L : 4321L } },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    created = new { levelId = dry ? -1L : 4321L, name = "Этаж 09" }
                }),
                "level");
        });
        var planner = new ScriptedTool("create_floor_plans", schema:
            @"{""type"":""object"",""properties"":{""levels"":{""type"":""array""," +
            @"""items"":{""type"":""integer""}}},""additionalProperties"":false}");
        var executor = Executor(creator, planner);

        var steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "s1",
                ToolName = "create_level",
                ArgumentsJson = @"{""name"":""Этаж 09"",""elevationMm"":28500}",
                ExpectedChanges = 1
            },
            new AgentPlanStep
            {
                StepId = "s2",
                ToolName = "create_floor_plans",
                ArgumentsJson = @"{""levels"":[{""$step"":""s1"",""path"":""created.levelId""}]}",
                DependsOn = new[] { "s1" }
            }
        };

        var result = await executor.ExecuteAsync(
            new AgentPlan { Goal = "new floor", Steps = steps }, Context(), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var plannerArgs = planner.Executions.Single().ArgumentsJson;
        Assert.Contains("4321", plannerArgs, StringComparison.Ordinal);
        Assert.True(result.Steps.Single(step => step.StepId == "s2").PreviewSkipped);
        // With the dependency declared, the honest reason is the unapplied write;
        // uses_step_reference covers references whose target step is not a write.
        Assert.Contains(
            "s1",
            result.Steps.Single(step => step.StepId == "s2").PreviewSkipReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StepReference_ToFailedStep_FailsTyped()
    {
        var failing = new ScriptedTool("create_level", schema:
            @"{""type"":""object"",""properties"":{""name"":{""type"":""string""}," +
            @"""elevationMm"":{""type"":""number""}},""additionalProperties"":false}",
            handler: args =>
        {
            var dry = args.TryGetProperty("dryRun", out var flag) && flag.ValueKind == JsonValueKind.True;
            return dry
                ? ToolResult.Ok(
                    JsonSerializer.SerializeToElement(new
                    {
                        changed = new[] { new { id = -1L } },
                        skipped = Array.Empty<object>(),
                        failed = Array.Empty<object>(),
                        created = new { levelId = -1L }
                    }), "dry ok")
                : ToolResult.Fail("level_create_failed", "boom");
        });
        var planner = new ScriptedTool("create_floor_plans", schema:
            @"{""type"":""object"",""properties"":{""levels"":{""type"":""array""," +
            @"""items"":{""type"":""integer""}}},""additionalProperties"":false}");
        var executor = Executor(failing, planner);

        var steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "s1",
                ToolName = "create_level",
                ArgumentsJson = @"{""name"":""X"",""elevationMm"":2800}",
                ExpectedChanges = 1
            },
            new AgentPlanStep
            {
                StepId = "s2",
                ToolName = "create_floor_plans",
                ArgumentsJson = @"{""levels"":[{""$step"":""s1"",""path"":""created.levelId""}]}",
                DependsOn = new[] { "s1" }
            }
        };

        var result = await executor.ExecuteAsync(
            new AgentPlan { Goal = "new floor", Steps = steps }, Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(planner.Executions);
        // The failing create_level aborts the plan and its dependent is marked
        // upstream_step_failed before the reference could even be resolved; the
        // typed step_reference_unresolved wording is covered by the pure
        // resolver tests above.
        Assert.Equal(
            "upstream_step_failed",
            result.Steps.Single(step => step.StepId == "s2").ApplyResult!.ErrorCode);
    }

    // ── template-parameter conflict ───────────────────────────────────────

    [Fact]
    public async Task PlanStepConflict_ParameterDetachedByInclusionStep_AllowsTheWrite()
    {
        // The remedy the conflict message recommends must actually unblock the
        // plan: set_view_template_inclusion detaching the parameter releases the
        // lock, otherwise the retried plan would hit the same conflict again
        // (S.42 validator finding).
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
                            controls = new { crop = false, visibility = false, viewRange = false },
                            overwrittenSample = new[] { "ADSK_Штамп Раздел проекта" }
                        }
                    }
                }),
                "template"));
        var inclusionTool = new ScriptedTool("set_view_template_inclusion", schema:
            @"{""type"":""object"",""properties"":{""templateId"":{""type"":""integer""}," +
            @"""parameters"":{""type"":""array"",""items"":{""type"":""string""}}," +
            @"""included"":{""type"":""boolean""}},""additionalProperties"":false}");
        var parameterTool = new ScriptedTool("set_view_parameters", schema:
            @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array""," +
            @"""items"":{""type"":""integer""}},""parameters"":{""type"":""array""," +
            @"""items"":{""type"":""object""}}},""additionalProperties"":false}");
        var executor = Executor(templateTool, inclusionTool, parameterTool);

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
                StepId = "inc",
                ToolName = "set_view_template_inclusion",
                ArgumentsJson =
                    @"{""templateId"":9,""parameters"":[""ADSK_Штамп Раздел проекта""],""included"":false}",
                DependsOn = new[] { "tpl" },
                ExpectedChanges = 1
            },
            new AgentPlanStep
            {
                StepId = "s3",
                ToolName = "set_view_parameters",
                ArgumentsJson =
                    @"{""viewIds"":[1],""parameters"":[{""name"":""ADSK_Штамп Раздел проекта"",""value"":""Вентиляция""}]}",
                DependsOn = new[] { "tpl", "inc" }
            }
        };

        var result = await executor.ExecuteAsync(
            new AgentPlan { Goal = "plan B fixed", Steps = steps }, Context(), CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }


    [Fact]
    public async Task PlanStepConflict_TemplateControlsTheWrittenParameter()
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
                            templateName = "В_ПЭ_Отопление",
                            controls = new { crop = false, visibility = false, viewRange = false },
                            overwrittenSample = new[] { "ADSK_Штамп Раздел проекта", "Масштаб" }
                        }
                    }
                }),
                "template"));
        var parameterTool = new ScriptedTool("set_view_parameters", schema:
            @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array""," +
            @"""items"":{""type"":""integer""}},""parameters"":{""type"":""array""," +
            @"""items"":{""type"":""object""}}},""additionalProperties"":false}");
        var executor = Executor(templateTool, parameterTool);

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
                StepId = "s2",
                ToolName = "set_view_parameters",
                ArgumentsJson =
                    @"{""viewIds"":[1],""parameters"":[{""name"":""ADSK_Штамп Раздел проекта"",""value"":""Вентиляция""}]}",
                DependsOn = new[] { "tpl" }
            }
        };

        var result = await executor.ExecuteAsync(
            new AgentPlan { Goal = "plan B", Steps = steps }, Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_step_conflict", result.ErrorCode);
        Assert.Contains("ADSK_Штамп Раздел проекта", result.Message, StringComparison.Ordinal);
        Assert.Contains("set_view_template_inclusion", result.Message, StringComparison.Ordinal);
        Assert.Contains("tpl", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanStepConflict_ParameterOutsideTheTemplate_AllowsTheWrite()
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
                            controls = new { crop = false, visibility = false, viewRange = false },
                            overwrittenSample = new[] { "Масштаб" }
                        }
                    }
                }),
                "template"));
        var parameterTool = new ScriptedTool("set_view_parameters", schema:
            @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array""," +
            @"""items"":{""type"":""integer""}},""parameters"":{""type"":""array""," +
            @"""items"":{""type"":""object""}}},""additionalProperties"":false}");
        var executor = Executor(templateTool, parameterTool);

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
                StepId = "s2",
                ToolName = "set_view_parameters",
                ArgumentsJson =
                    @"{""viewIds"":[1],""parameters"":[{""name"":""Другой параметр"",""value"":""x""}]}",
                DependsOn = new[] { "tpl" }
            }
        };

        var result = await executor.ExecuteAsync(
            new AgentPlan { Goal = "plan", Steps = steps }, Context(), CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }

    private static AgentPlanExecutionContext Context() =>
        new() { DocumentId = "doc", ApprovalMode = AgentApprovalMode.ConfirmWrites };
}
