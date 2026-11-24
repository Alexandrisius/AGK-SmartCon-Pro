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
public sealed partial class ExecutePlanTool : IAgentTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Closes every preview hint (complete, with skipped dependent steps and
    /// failed). The report renames only the keys a dry-run result marks as
    /// created; the other ids of a creating step are dry-run ids as well and the
    /// model has to know it (review r1 #4).
    /// </summary>
    private const string DryRunIdHint =
        " Dry-run ids do not survive apply: the dry run rolled back, so the apply " +
        "creates new ids. Renamed with a preview prefix: the created object's own id " +
        "inside created* blocks (created[].previewViewId, created.previewScheduleId) " +
        "and wouldCreate*Id keys. Not renamed but dry-run ids as well: ids outside " +
        "those keys, for example changed[].id of a creating " +
        "step. Never reuse them in later steps or read-back arguments — address " +
        "created objects by name or re-read them after apply.";

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
            "Every step must be an object with the required keys in this order: " +
            "{\"stepId\":\"s1\",\"toolName\":\"set_view_template\",\"arguments\":{...}} " +
            "(toolName names the tool to run; dependsOn is optional and only for steps whose " +
            "arguments actually read an earlier step's result). " +
            "To configure what an earlier step CREATED, reference its result inside arguments: " +
            "{\"$step\":\"<stepId>\",\"path\":\"created.sheetId\"} (dot segments or array " +
            "indexes: createdViewports.0.viewportId). At apply the reference is replaced by the REAL " +
            "id from that step's apply result, so create_sheet → create_viewports or " +
            "create_schedule → place_schedule_on_sheet stay one plan; the referenced steps are " +
            "wired into dependsOn automatically. Reference the tool's own created* block: " +
            "create_level → created.levelId, create_sheet → created.sheetId, create_schedule → " +
            "created.scheduleId, create_viewports → createdViewports.N.viewportId. In the preview " +
            "such steps are preview_skipped (a dry-run id does not survive apply) and an " +
            "unresolvable reference fails the step with step_reference_unresolved. " +
            "onError:\"continue\" marks a step whose failure is EXPECTED (a negative guard " +
            "check, e.g. copy_crop_from_view on a template-controlled view): the failure is " +
            "recorded (steps[].expectedFailure=true) and the remaining steps still run; the " +
            "default onError:\"stop\" aborts the plan on the first failed step. " +
            "With dryRun=true (default) every write step is executed as a rollback-only dry run " +
            "and the aggregated diff (changed/skipped/failed per step) is returned; nothing " +
            "changes. Show that diff to the user. Call again with dryRun=false and the " +
            "planDigest from the preview only after the user approves: the steps are applied in " +
            "dependency order, one change set per step (undo_last works per step) and a " +
            "read-back verifies the result. If the tool-level confirmation already approved the " +
            "exact arguments, planDigest may be omitted: it is derived from those arguments " +
            "(planDigestAutoFilled=true) and the apply runs without a separate preview. Steps " +
            "that depend on an earlier write are marked preview_skipped in the preview and run " +
            "only during apply — use dependsOn only when the step really consumes the earlier " +
            "result, otherwise you lose its preview. Never include execute_plan as a step. " +
            "readBackExpectations need a readBackTool; path is dot-addressed from its data: " +
            "views.byId[123].templateName (byId matches the row's own id key: id, viewId, " +
            "scheduleId...), views.byName[View 1.2].templateName (a dot inside brackets does " +
            "not split the path), types.byId[123].values.byName[param name] (flat value map) " +
            "or values.[param name]. A path that addresses nothing in the payload is a " +
            "mismatch naming the missing row or key; only an unsupported path form is " +
            "notVerifiable. " +
            "expectedChanges/expectedChangesAtLeast are optional; when set they must equal " +
            "steps[].changed of the step's preview — the tool's own change count (a creating " +
            "tool counts the object and each field). items.changed[] may be shorter: cards " +
            "that only duplicate a created* block are dropped and counted in " +
            "items.duplicatesInCreated. " +
            "Dry-run ids do not survive apply. The preview renames only the created object's " +
            "own id inside created* blocks (created[].previewViewId, created.previewScheduleId) " +
            "and wouldCreate*Id keys, never referencing ids such as levelId or templateId. " +
            "Other ids are not renamed but are dry-run ids too (" +
            "changed[].id of a creating step): never reuse them in later steps or read-back " +
            "arguments — address created objects by name or re-read them after apply. " +
            "Family write tools already do their own read-back after LoadFamily; the apply " +
            "answer counts it as planReadBack.toolVerified, so verified is not zero just " +
            "because no plan-level expectation was declared.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        Category = "Write",
        Tags = new[] { "план", "сценарий", "несколько", "шагов", "сразу", "plan", "multi-step" },
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""goal"":{""type"":""string""},""allowDestructive"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true},""planDigest"":{""type"":""string""},""steps"":{""type"":""array"",""minItems"":1,""maxItems"":20,""items"":{""type"":""object"",""properties"":{""stepId"":{""type"":""string""},""toolName"":{""type"":""string""},""arguments"":{""type"":""object""},""dependsOn"":{""type"":""array"",""items"":{""type"":""string""}},""onError"":{""type"":""string"",""enum"":[""stop"",""continue""],""default"":""stop""},""expectedChanges"":{""type"":""integer"",""minimum"":0},""expectedChangesAtLeast"":{""type"":""integer"",""minimum"":1},""rationale"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true},""readBackTool"":{""type"":""string""},""readBackArguments"":{""type"":""object""},""readBackExpectations"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""path"":{""type"":""string""},""expected"":{""type"":""string""}},""required"":[""path"",""expected""],""additionalProperties"":false}}},""required"":[""stepId"",""toolName"",""arguments""],""additionalProperties"":false}}},""required"":[""goal"",""steps""],""additionalProperties"":false}"
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
        var warnings = ReadBackArgumentWarnings(plan);

        var digest = AgentPlanExecutor.ComputeDigest(plan, context.DocumentId);
        var digestAutoFilled = false;
        if (!dryRun)
        {
            var presentedDigest = GetString(args, "planDigest");
            if (string.IsNullOrWhiteSpace(presentedDigest))
            {
                // The outer executor already showed the confirmation for these exact
                // arguments (WriteApproved), so re-deriving the same digest adds a
                // round without adding safety. Keep strict comparison when a digest
                // is supplied: it catches an edited plan after the preview.
                digestAutoFilled = context.WriteApproved;
                if (!digestAutoFilled)
                {
                    return ToolResult.Fail(
                        "plan_digest_required",
                        "Pass planDigest from the dry-run preview so the applied plan is the plan " +
                        "the user saw. Current digest: " + digest);
                }
            }
            else if (!string.Equals(presentedDigest, digest, StringComparison.Ordinal))
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
                data: FailureResult(plan, result, digest, dryRun, digestAutoFilled));
        }

        return dryRun
            ? PreviewResult(plan, result, warnings)
            : ApplyResult(result, digestAutoFilled, warnings);
    }

    /// <summary>
    /// readBackArguments without a readBackTool and without expectations used to
    /// be dropped silently: the model believed it had verified the step. The
    /// warning names the step so the arguments get a tool or get removed
    /// (review r2, WA-7).
    /// </summary>
    private static IReadOnlyList<string> ReadBackArgumentWarnings(AgentPlan plan)
    {
        var warnings = new List<string>();
        foreach (var step in plan.Steps)
        {
            if (!string.IsNullOrWhiteSpace(step.ReadBackTool)
                || step.ReadBackExpectations.Count > 0
                || string.IsNullOrWhiteSpace(step.ArgumentsReadBackJson))
            {
                continue;
            }

            var trimmed = step.ArgumentsReadBackJson.Trim();
            if (trimmed.Length == 0
                || string.Equals(trimmed, "{}", StringComparison.Ordinal))
            {
                continue;
            }

            warnings.Add(
                "Step '" + step.StepId + "': readBackArguments ignored: no readBackTool was " +
                "declared, so no read-back ran for them.");
        }

        return warnings;
    }

    private static JsonElement FailureResult(
        AgentPlan plan,
        AgentPlanExecutionResult result,
        string digest,
        bool dryRun,
        bool digestAutoFilled)
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
            planDigestAutoFilled = digestAutoFilled,
            appliedWithoutPreview = !dryRun && digestAutoFilled,
            errorCode = result.ErrorCode,
            message = result.Message,
            affected = result.AffectedCount > 0
                ? result.AffectedCount
                : result.Steps.Sum(step => step.Changed),
            failedStepId,
            expectedFailures = result.ExpectedFailureCount,
            steps = Steps(result, apply: !dryRun),
            previewIncomplete = result.PreviewIncomplete,
            notPreviewed = result.NotPreviewed,
            hint = dryRun
                ? "Nothing was changed. Fix the failed step and preview again." + DryRunIdHint
                : result.Steps.Count == 0
                    // Rejected by validation before any step ran (unknown tool,
                    // expectations without readBackTool…): nothing to undo.
                    ? "Nothing was changed: the plan was rejected before any step ran. " +
                      "Fix the plan and run it again."
                    : "Steps with status 'applied' already have their own change sets; use " +
                      "undo_last to revert them. Steps with status 'not_run' were not executed."
        }, SerializerOptions);

        return payload;
    }

    private static ToolResult PreviewResult(
        AgentPlan plan,
        AgentPlanExecutionResult result,
        IReadOnlyList<string> warnings)
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
            expectedFailures = result.ExpectedFailureCount,
            steps = Steps(result, apply: false),
            warnings,
            // The dry-run id warning closes every preview branch: the incomplete
            // one is exactly where the model chains ids into dependent steps, and
            // a failed preview reports the same steps[] (review r1 #4).
            hint = (result.PreviewIncomplete
                ? "Nothing was changed. Step(s) " + string.Join(", ", result.NotPreviewed) +
                  " depend on an earlier write and were skipped in the preview " +
                  "(status preview_skipped); they run only during apply. Show the previewed " +
                  "diff and call execute_plan with the same steps, dryRun=false and planDigest=" +
                  result.PlanDigest + " only after explicit approval."
                : "Nothing was changed. Show this diff to the user and call execute_plan with " +
                  "the same steps, dryRun=false and planDigest=" + result.PlanDigest +
                  " only after explicit approval. Per-step dryRun flags do not affect the digest.") +
                  DryRunIdHint +
                  (warnings.Count == 0
                      ? string.Empty
                      : " " + string.Join(" ", warnings))
        }, SerializerOptions);

        return ToolResult.Ok(
            payload,
            "Plan preview: " + plan.Steps.Count + " step(s), " + result.AffectedCount +
            " change(s), dry run rolled back." +
            (result.PreviewIncomplete
                ? " " + result.NotPreviewed.Count + " dependent step(s) were not previewed."
                : string.Empty));
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
                // A {"$step": …} reference is not the final value: repairing the
                // arguments would drop the reference as an unknown property. The
                // resolved arguments are schema-validated at apply instead.
                if (AgentPlanExecutor.ContainsStepReference(step.ArgumentsJson)
                    || AgentPlanExecutor.ContainsStepReference(step.ArgumentsReadBackJson))
                {
                    steps.Add(step);
                    continue;
                }

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
                    OnError = step.OnError,
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
                DependsOn = WireStepReferences(
                    stepId,
                    GetStringArray(stepElement, "dependsOn"),
                    arguments.GetRawText(),
                    GetObjectJson(stepElement, "readBackArguments")),
                OnError = GetString(stepElement, "onError") ?? "stop",
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

    /// <summary>
    /// A {"$step": "s1"} reference in a step's arguments means the step consumes
    /// s1's apply result: it must run after s1, so the referenced ids are wired
    /// into dependsOn automatically — the model never has to spell the
    /// dependency out (and forgetting it would break the topological order the
    /// apply follows) (S.42).
    /// </summary>
    private static IReadOnlyList<string> WireStepReferences(
        string stepId,
        IReadOnlyList<string> dependsOn,
        string argumentsJson,
        string readBackArgumentsJson)
    {
        var wired = new List<string>(dependsOn);
        foreach (var referenced in AgentPlanExecutor.ReferencedStepIds(argumentsJson))
        {
            if (!string.Equals(referenced, stepId, StringComparison.Ordinal)
                && !wired.Contains(referenced, StringComparer.Ordinal))
            {
                wired.Add(referenced);
            }
        }

        foreach (var referenced in AgentPlanExecutor.ReferencedStepIds(readBackArgumentsJson))
        {
            if (!string.Equals(referenced, stepId, StringComparison.Ordinal)
                && !wired.Contains(referenced, StringComparer.Ordinal))
            {
                wired.Add(referenced);
            }
        }

        return wired;
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
