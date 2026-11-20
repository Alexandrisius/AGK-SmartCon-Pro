using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

/// <summary>
/// Meta-tool that turns the LLM's multi-step plan into a typed
/// <see cref="AgentPlan"/> and runs the L0–L4 pipeline: schema, preflight,
/// dry-run fidelity. With <c>dryRun=false</c> (only after the outer tool-level
/// confirmation approved the exact plan arguments) it also applies the steps and
/// performs the L6 read-back. The plan executor still creates one change set per
/// write step, so undo_last keeps working.
/// </summary>
public sealed class ExecutePlanTool : IAgentTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<IToolRegistry> _registryFactory;
    private readonly IAgentAwaitableEvent _revit;
    private readonly IAgentAuditLog? _auditLog;
    private readonly AgentSessionContext? _sessionContext;

    public ExecutePlanTool(
        Func<IToolRegistry> registryFactory,
        IAgentAwaitableEvent revit,
        IAgentAuditLog? auditLog = null,
        AgentSessionContext? sessionContext = null)
    {
        _registryFactory = registryFactory ?? throw new ArgumentNullException(nameof(registryFactory));
        _revit = revit ?? throw new ArgumentNullException(nameof(revit));
        _auditLog = auditLog;
        _sessionContext = sessionContext;
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "execute_plan",
        Description =
            "Validates and runs a multi-step plan (1–20 steps) as one reviewable unit. " +
            "With dryRun=true (default) every write step is executed as a rollback-only dry run " +
            "and the aggregated diff (changed/skipped/failed per step) is returned; nothing " +
            "changes. Show that diff to the user. Call again with dryRun=false and the " +
            "planDigest from the preview only after the user approves: the steps are applied in " +
            "dependency order, one change set per step (undo_last works per step) and a " +
            "read-back verifies the result. Steps that depend on an earlier write are marked " +
            "preview_skipped in the preview and run only during apply. Never include " +
            "execute_plan as a step. readBackExpectations.path is dot-addressed from the " +
            "read-back tool's data object (for example views.byId[123].templateName); use " +
            "byId[<elementId>].property when the read-back returns an array (index access " +
            "can check the wrong element).",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        Category = "Write",
        Tags = new[] { "план", "сценарий", "несколько", "шагов", "сразу", "plan", "multi-step" },
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""goal"":{""type"":""string""},""allowDestructive"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true},""planDigest"":{""type"":""string""},""steps"":{""type"":""array"",""minItems"":1,""maxItems"":20,""items"":{""type"":""object"",""properties"":{""stepId"":{""type"":""string""},""toolName"":{""type"":""string""},""arguments"":{""type"":""object""},""dependsOn"":{""type"":""array"",""items"":{""type"":""string""}},""expectedChanges"":{""type"":""integer"",""minimum"":0},""expectedChangesAtLeast"":{""type"":""integer"",""minimum"":1},""rationale"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true},""readBackTool"":{""type"":""string""},""readBackArguments"":{""type"":""object""},""readBackExpectations"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""path"":{""type"":""string""},""expected"":{""type"":""string""}},""required"":[""path"",""expected""],""additionalProperties"":false}}},""required"":[""stepId"",""toolName"",""arguments""],""additionalProperties"":false}}},""required"":[""goal"",""steps""],""additionalProperties"":false}"
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var schemaErrors = AgentToolSchemaValidator.Validate(
            Definition.InputSchemaJson,
            args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText());
        if (schemaErrors.Count > 0)
        {
            return ToolResult.Fail(
                "plan_invalid",
                "Plan arguments are invalid: " + string.Join("; ", schemaErrors));
        }

        var dryRun = !args.TryGetProperty("dryRun", out var dryRunProperty)
                      || dryRunProperty.ValueKind != JsonValueKind.False;

        if (!dryRun && !context.WriteApproved)
        {
            return ToolResult.Fail(
                "confirmation_required",
                "Applying a plan requires explicit user confirmation of the plan arguments.");
        }

        AgentPlan plan;
        try
        {
            plan = ParsePlan(args);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail("plan_invalid", ex.Message);
        }

        // A direct tool call repairs unambiguous argument mistakes (number ->
        // string, aliases, unknown properties). Plan steps must behave the same,
        // otherwise the same arguments fail only when wrapped in a plan. Repair
        // happens before the digest, so preview and apply see identical steps.
        var registry = _registryFactory();
        plan = RepairPlanSteps(plan, registry);

        var digest = AgentPlanExecutor.ComputeDigest(plan, context.DocumentId);
        if (!dryRun)
        {
            var presentedDigest = GetString(args, "planDigest");
            if (string.IsNullOrWhiteSpace(presentedDigest))
            {
                return ToolResult.Fail(
                    "plan_digest_required",
                    "Pass planDigest from the dry-run preview so the applied plan is the plan " +
                    "the user saw. Current digest: " + digest);
            }

            if (!string.Equals(presentedDigest, digest, StringComparison.Ordinal))
            {
                return ToolResult.Fail(
                    "plan_digest_mismatch",
                    "The plan changed after the preview. Run execute_plan with dryRun=true " +
                    "again and ask the user to approve the updated plan.");
            }
        }

        var executor = new AgentPlanExecutor(
            registry,
            _revit,
            new AutoApprovedConfirmation(),
            _auditLog);

        var execution = new AgentPlanExecutionContext
        {
            DocumentId = context.DocumentId,
            ConversationId = context.ConversationId,
            ApprovalMode = _sessionContext?.ApprovalMode ?? AgentApprovalMode.ConfirmWrites
        };

        var result = dryRun
            ? await executor.PreviewAsync(plan, execution, ct).ConfigureAwait(false)
            : await executor.ExecuteAsync(plan, execution, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return ToolResult.Fail(
                result.ErrorCode ?? "plan_failed",
                result.Message ?? "The plan could not be executed.",
                data: FailureResult(plan, result, digest, dryRun));
        }

        return dryRun ? PreviewResult(plan, result) : ApplyResult(result);
    }

    private static JsonElement FailureResult(
        AgentPlan plan,
        AgentPlanExecutionResult result,
        string digest,
        bool dryRun)
    {
        var failedStepId = result.Steps
            .FirstOrDefault(step =>
                (dryRun ? step.DryRunResult : step.ApplyResult) is { Success: false })
            ?.StepId;

        var payload = JsonSerializer.SerializeToElement(new
        {
            dryRun,
            phase = dryRun ? "preview" : "apply",
            goal = plan.Goal,
            planDigest = digest,
            planDigestAlgorithm = "sha256",
            errorCode = result.ErrorCode,
            message = result.Message,
            affected = result.AffectedCount > 0
                ? result.AffectedCount
                : result.Steps.Sum(step => step.Changed),
            failedStepId,
            steps = Steps(result, apply: !dryRun),
            previewIncomplete = result.PreviewIncomplete,
            notPreviewed = result.NotPreviewed,
            hint = dryRun
                ? "Nothing was changed. Fix the failed step and preview again."
                : "Steps with status 'applied' already have their own change sets; use " +
                  "undo_last to revert them. Steps with status 'not_run' were not executed."
        }, SerializerOptions);

        return payload;
    }

    private static ToolResult PreviewResult(AgentPlan plan, AgentPlanExecutionResult result)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            dryRun = true,
            goal = plan.Goal,
            planDigest = result.PlanDigest,
            planDigestAlgorithm = "sha256",
            affected = result.AffectedCount,
            previewIncomplete = result.PreviewIncomplete,
            notPreviewed = result.NotPreviewed,
            steps = Steps(result, apply: false),
            hint = result.PreviewIncomplete
                ? "Nothing was changed. Step(s) " + string.Join(", ", result.NotPreviewed) +
                  " depend on an earlier write and were skipped in the preview " +
                  "(status preview_skipped); they run only during apply. Show the previewed " +
                  "diff and call execute_plan with the same steps, dryRun=false and planDigest=" +
                  result.PlanDigest + " only after explicit approval."
                : "Nothing was changed. Show this diff to the user and call execute_plan with " +
                  "the same steps, dryRun=false and planDigest=" + result.PlanDigest +
                  " only after explicit approval. Per-step dryRun flags do not affect the digest."
        }, SerializerOptions);

        return ToolResult.Ok(
            payload,
            "Plan preview: " + plan.Steps.Count + " step(s), " + result.AffectedCount +
            " change(s), dry run rolled back." +
            (result.PreviewIncomplete
                ? " " + result.NotPreviewed.Count + " dependent step(s) were not previewed."
                : string.Empty));
    }

    private static ToolResult ApplyResult(AgentPlanExecutionResult result)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            dryRun = false,
            planDigest = result.PlanDigest,
            planDigestAlgorithm = "sha256",
            affected = result.AffectedCount,
            previewIncomplete = result.PreviewIncomplete,
            notPreviewed = result.NotPreviewed,
            steps = Steps(result, apply: true),
            planReadBack = new
            {
                executedSteps = result.Steps.Count(step => step.ReadBackExecuted),
                expectationsChecked = result.ExpectationsChecked,
                verified = result.Verified.Count,
                mismatches = result.Mismatches.Count
            },
            verified = result.Verified,
            mismatches = result.Mismatches,
            hint = result.Mismatches.Count > 0
                ? "Read-back found mismatches. Review them and offer undo_last for the " +
                  "affected step."
                : result.ExpectationsChecked == 0
                    ? "No plan read-back expectations were declared, so there are no verified " +
                      "values; tool-internal read-back (if any) is in each step's toolReadBack."
                    : "All " + result.ExpectationsChecked +
                      " plan read-back expectation(s) verified."
        }, SerializerOptions);

        return ToolResult.Ok(
            payload,
            "Plan applied: " + result.AffectedCount + " change(s); " +
            result.Verified.Count + " verified, " + result.Mismatches.Count + " mismatch(es).",
            result.AffectedCount);
    }

    private static IReadOnlyList<object> Steps(AgentPlanExecutionResult result, bool apply) =>
        result.Steps.Select(step => (object)new
        {
            stepId = step.StepId,
            toolName = step.ToolName,
            status = Status(step, apply),
            changed = step.Changed,
            skipped = step.Skipped,
            failed = step.Failed,
            items = StepItems(step, apply),
            toolReadBack = ToolReadBack(step, apply),
            planReadBack = new
            {
                executed = step.ReadBackExecuted,
                expectationsChecked = step.ExpectationsChecked,
                verified = step.VerifiedCount,
                mismatches = step.MismatchCount
            },
            fidelity = step.FidelityChecked
                ? new
                {
                    expected = step.FidelityExpected,
                    matched = step.FidelityMatched
                }
                : null,
            preflightSkipped = step.PreflightSkipped,
            preflightDeferred = step.PreflightDeferred,
            previewSkipped = step.PreviewSkipped,
            previewSkipReason = step.PreviewSkipReason
        }).ToList();

    private static string Status(AgentPlanStepResult step, bool apply)
    {
        if (apply)
        {
            if (step.ApplyResult is null)
                return "not_run";

            return step.ApplyResult.Success ? "applied" : "failed";
        }

        if (step.DryRunResult is not null)
            return step.DryRunResult.Success ? "previewed" : "preview_failed";

        return step.PreviewSkipped ? "preview_skipped" : "not_previewed";
    }

    private static JsonElement? ToolReadBack(AgentPlanStepResult step, bool apply)
    {
        var result = apply
            ? step.ApplyResult ?? step.DryRunResult
            : step.DryRunResult ?? step.ApplyResult;
        if (result?.Data is not { ValueKind: JsonValueKind.Object } data)
            return null;

        return data.TryGetProperty("readBack", out var readBack) ? readBack : null;
    }

    /// <summary>
    /// Per-item diff from the step's dry run (or apply): changed / skipped / failed
    /// cards with id, name, before and after. Without it the model has to re-read
    /// the model just to show the user what will change (live dogfood P1-4).
    /// </summary>
    private static JsonElement? StepItems(AgentPlanStepResult step, bool apply)
    {
        // Preview shows the dry-run diff; the applied report must show the apply
        // result, otherwise every item still says dryRun=true (feedback #2).
        var result = apply
            ? step.ApplyResult ?? step.DryRunResult
            : step.DryRunResult ?? step.ApplyResult;
        if (result?.Data is not { ValueKind: JsonValueKind.Object } data)
            return null;

        if (!data.TryGetProperty("changed", out _)
            && !data.TryGetProperty("skipped", out _)
            && !data.TryGetProperty("failed", out _))
        {
            return null;
        }

        return data;
    }

    private AgentPlan RepairPlanSteps(AgentPlan plan, IToolRegistry registry)
    {
        var steps = new List<AgentPlanStep>(plan.Steps.Count);
        foreach (var step in plan.Steps)
        {
            if (!registry.TryGet(step.ToolName, out var tool) || tool is null)
            {
                steps.Add(step);
                continue;
            }

            try
            {
                using var arguments = JsonDocument.Parse(step.ArgumentsJson);
                var repair = AgentToolInputRepair.Repair(
                    tool.Definition.InputSchemaJson, arguments.RootElement);
                if (!repair.Changed || repair.Arguments.ValueKind != JsonValueKind.Object)
                {
                    steps.Add(step);
                    continue;
                }

                steps.Add(new AgentPlanStep
                {
                    StepId = step.StepId,
                    ToolName = step.ToolName,
                    ArgumentsJson = repair.Arguments.GetRawText(),
                    DependsOn = step.DependsOn,
                    DryRun = step.DryRun,
                    Rationale = step.Rationale,
                    ExpectedChanges = step.ExpectedChanges,
                    ExpectedChangesAtLeast = step.ExpectedChangesAtLeast,
                    ReadBackTool = step.ReadBackTool,
                    ArgumentsReadBackJson = step.ArgumentsReadBackJson,
                    ReadBackExpectations = step.ReadBackExpectations
                });
            }
            catch (JsonException)
            {
                steps.Add(step);
            }
        }

        return new AgentPlan
        {
            Goal = plan.Goal,
            Steps = steps,
            AllowDestructive = plan.AllowDestructive
        };
    }

    private static AgentPlan ParsePlan(JsonElement args)
    {
        if (!args.TryGetProperty("goal", out var goalElement)
            || goalElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("goal is required.");
        }

        if (!args.TryGetProperty("steps", out var stepsElement)
            || stepsElement.ValueKind != JsonValueKind.Array
            || stepsElement.GetArrayLength() == 0)
        {
            throw new ArgumentException("steps must be a non-empty array.");
        }

        var steps = new List<AgentPlanStep>();
        foreach (var stepElement in stepsElement.EnumerateArray())
        {
            var stepId = GetString(stepElement, "stepId")
                         ?? throw new ArgumentException("Every step needs a stepId.");
            var toolName = GetString(stepElement, "toolName")
                           ?? throw new ArgumentException("Step '" + stepId + "' needs a toolName.");

            if (string.Equals(toolName, "execute_plan", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A plan step cannot be execute_plan itself.");

            if (!stepElement.TryGetProperty("arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "Step '" + stepId + "' needs an arguments object.");
            }

            steps.Add(new AgentPlanStep
            {
                StepId = stepId,
                ToolName = toolName,
                ArgumentsJson = arguments.GetRawText(),
                DependsOn = GetStringArray(stepElement, "dependsOn"),
                ExpectedChanges = GetInt(stepElement, "expectedChanges"),
                ExpectedChangesAtLeast = GetInt(stepElement, "expectedChangesAtLeast"),
                Rationale = GetString(stepElement, "rationale"),
                DryRun = GetBool(stepElement, "dryRun", true),
                ReadBackTool = GetString(stepElement, "readBackTool"),
                ArgumentsReadBackJson = GetObjectJson(stepElement, "readBackArguments"),
                ReadBackExpectations = GetExpectations(stepElement)
            });
        }

        return new AgentPlan
        {
            Goal = goalElement.GetString() ?? string.Empty,
            Steps = steps,
            AllowDestructive = args.TryGetProperty("allowDestructive", out var allow)
                               && allow.ValueKind == JsonValueKind.True
        };
    }

    private static IReadOnlyList<AgentPlanExpectation> GetExpectations(JsonElement step)
    {
        if (!step.TryGetProperty("readBackExpectations", out var expectations)
            || expectations.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AgentPlanExpectation>();
        }

        var result = new List<AgentPlanExpectation>();
        foreach (var expectation in expectations.EnumerateArray())
        {
            var path = GetString(expectation, "path");
            var expected = GetString(expectation, "expected");
            if (path is null || expected is null)
            {
                throw new ArgumentException(
                    "readBackExpectations entries need string path and expected values.");
            }

            result.Add(new AgentPlanExpectation { Path = path, Expected = expected });
        }

        return result;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool GetBool(JsonElement element, string property, bool fallback) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static string GetObjectJson(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.GetRawText()
            : "{}";

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToList();
    }

    /// <summary>
    /// The outer tool executor already verified the exact arguments (or the
    /// session bypass did), so the plan executor's own approval request is
    /// satisfied without a second dialog.
    /// </summary>
    private sealed class AutoApprovedConfirmation : IAgentConfirmationService
    {
        public Task<AgentConfirmationResult> RequestAsync(
            AgentConfirmationRequest request,
            CancellationToken ct) =>
            Task.FromResult(AgentConfirmationResult.Approved);
    }
}
