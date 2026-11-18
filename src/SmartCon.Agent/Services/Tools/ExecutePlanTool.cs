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
            "read-back verifies the result. Never include execute_plan as a step.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        Category = "Write",
        Tags = new[] { "план", "сценарий", "несколько", "шагов", "сразу", "plan", "multi-step" },
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""goal"":{""type"":""string""},""allowDestructive"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true},""planDigest"":{""type"":""string""},""steps"":{""type"":""array"",""minItems"":1,""maxItems"":20,""items"":{""type"":""object"",""properties"":{""stepId"":{""type"":""string""},""toolName"":{""type"":""string""},""arguments"":{""type"":""object""},""dependsOn"":{""type"":""array"",""items"":{""type"":""string""}},""expectedChanges"":{""type"":""integer"",""minimum"":0},""rationale"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true},""readBackTool"":{""type"":""string""},""readBackArguments"":{""type"":""object""},""readBackExpectations"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""path"":{""type"":""string""},""expected"":{""type"":""string""}},""required"":[""path"",""expected""],""additionalProperties"":false}}},""required"":[""stepId"",""toolName"",""arguments""],""additionalProperties"":false}}},""required"":[""goal"",""steps""],""additionalProperties"":false}"
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

        var registry = _registryFactory();
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
                result.Message ?? "The plan could not be executed.");
        }

        return dryRun ? PreviewResult(plan, result) : ApplyResult(result);
    }

    private static ToolResult PreviewResult(AgentPlan plan, AgentPlanExecutionResult result)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            dryRun = true,
            goal = plan.Goal,
            planDigest = result.PlanDigest,
            affected = result.AffectedCount,
            steps = Steps(result),
            hint = "Nothing was changed. Show this diff to the user and call execute_plan with " +
                   "the same steps, dryRun=false and planDigest=" + result.PlanDigest +
                   " only after explicit approval."
        }, SerializerOptions);

        return ToolResult.Ok(
            payload,
            "Plan preview: " + plan.Steps.Count + " step(s), " + result.AffectedCount +
            " change(s), dry run rolled back.");
    }

    private static ToolResult ApplyResult(AgentPlanExecutionResult result)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            dryRun = false,
            planDigest = result.PlanDigest,
            affected = result.AffectedCount,
            steps = Steps(result),
            verified = result.Verified,
            mismatches = result.Mismatches,
            hint = result.Mismatches.Count == 0
                ? "All expectations verified by the read-back."
                : "Read-back found mismatches. Review them and offer undo_last for the " +
                  "affected step."
        }, SerializerOptions);

        return ToolResult.Ok(
            payload,
            "Plan applied: " + result.AffectedCount + " change(s); " +
            result.Verified.Count + " verified, " + result.Mismatches.Count + " mismatch(es).",
            result.AffectedCount);
    }

    private static IReadOnlyList<object> Steps(AgentPlanExecutionResult result) =>
        result.Steps.Select(step => (object)new
        {
            stepId = step.StepId,
            toolName = step.ToolName,
            changed = step.Changed,
            skipped = step.Skipped,
            failed = step.Failed,
            fidelityMatched = step.FidelityChecked ? step.FidelityMatched : (bool?)null,
            preflightSkipped = step.PreflightSkipped
        }).ToList();

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
