using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Agent.Services.Tools;

namespace SmartCon.Agent.Services.Plans;

public sealed class AgentPlanStepResult
{
    public required string StepId { get; init; }

    public required string ToolName { get; init; }

    public bool DryRunOk { get; set; }

    public int Changed { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public bool PreflightSkipped { get; set; }

    public bool FidelityChecked { get; set; }

    public bool FidelityMatched { get; set; } = true;

    public ToolResult? DryRunResult { get; set; }

    public ToolResult? ApplyResult { get; set; }

    public ToolResult? ReadBackResult { get; set; }
}

public sealed class AgentPlanExecutionResult
{
    public bool Success { get; init; }

    public bool Approved { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public string PlanDigest { get; init; } = string.Empty;

    public IReadOnlyList<AgentPlanStepResult> Steps { get; init; } =
        Array.Empty<AgentPlanStepResult>();

    public IReadOnlyList<string> Verified { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();

    public int AffectedCount { get; init; }

    public static AgentPlanExecutionResult Fail(string code, string message) =>
        new() { Success = false, ErrorCode = code, Message = message };
}

/// <summary>
/// Executes a validated plan (L0) as: preflight (L1) → dry run with fidelity
/// check (L4) → single plan approval with nonce/expiry/digest (L3) → apply →
/// read-back (L6). Plan-level approval replaces per-tool confirmations; the
/// tools themselves still create change sets so undo_last works.
/// </summary>
public sealed class AgentPlanExecutor
{
    private readonly IToolRegistry _registry;
    private readonly IAgentAwaitableEvent _revit;
    private readonly IAgentConfirmationService _confirmation;
    private readonly AgentSessionContext _session;
    private readonly IAgentAuditLog? _audit;
    private readonly AgentConfirmationLedger _ledger = new();
    private readonly TimeSpan _approvalLifetime;

    public AgentPlanExecutor(
        IToolRegistry registry,
        IAgentAwaitableEvent revit,
        IAgentConfirmationService confirmation,
        AgentSessionContext session,
        IAgentAuditLog? audit = null,
        TimeSpan? approvalLifetime = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _revit = revit ?? throw new ArgumentNullException(nameof(revit));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _audit = audit;
        _approvalLifetime = approvalLifetime ?? TimeSpan.FromMinutes(5);
    }

    public async Task<AgentPlanExecutionResult> ExecuteAsync(
        AgentPlan plan,
        CancellationToken ct)
    {
        if (_session.ApprovalMode == AgentApprovalMode.ReadOnly)
            return AgentPlanExecutionResult.Fail(
                "read_only_mode",
                "The session is read-only. Switch the approval mode to apply the plan.");

        var validation = AgentPlanValidator.Validate(plan, _registry.GetDefinitions());
        if (!validation.IsValid)
        {
            await WriteAuditAsync(plan, "plan#validate", "invalid", validation.Errors.Count, ct)
                .ConfigureAwait(false);
            return AgentPlanExecutionResult.Fail(
                "plan_invalid",
                string.Join("; ", validation.Errors.Select(error => error.Code + ": " + error.Message)));
        }

        await WriteAuditAsync(plan, "plan#validate", "valid", 0, ct).ConfigureAwait(false);

        var order = AgentPlanValidator.OrderSteps(plan);
        var steps = order
            .Select(id => plan.Steps.First(step => step.StepId == id))
            .ToList();
        var results = steps
            .Select(step => new AgentPlanStepResult
            {
                StepId = step.StepId,
                ToolName = step.ToolName
            })
            .ToList();
        var resultById = results.ToDictionary(item => item.StepId, StringComparer.Ordinal);

        var preflight = await RunPreflightAsync(steps, results, ct).ConfigureAwait(false);
        if (preflight is not null)
            return preflight;

        var dryFailure = await RunDryRunAsync(steps, results, ct).ConfigureAwait(false);
        if (dryFailure is not null)
            return dryFailure;

        var writeResults = results
            .Where(item => IsWrite(item.ToolName))
            .ToList();
        var affected = writeResults.Sum(item => item.Changed);
        var digest = ComputePlanDigest(plan);

        if (writeResults.Count > 0)
        {
            var approval = await RequestApprovalAsync(plan, digest, affected, ct)
                .ConfigureAwait(false);
            if (approval is not null)
                return approval;
        }

        var applyFailure = await RunApplyAsync(steps, results, ct).ConfigureAwait(false);
        if (applyFailure is not null)
            return applyFailure;

        var (verified, mismatches) = await RunReadBackAsync(steps, results, ct).ConfigureAwait(false);

        if (mismatches.Count > 0)
        {
            SmartConLogger.Warn(
                "Agent plan " + plan.PlanId.ToString("N") + " read-back mismatches: " +
                string.Join("; ", mismatches) +
                " [Action: review the change set with undo_last; the model state differs from the plan]");
        }

        await WriteAuditAsync(
                plan, "plan#apply", mismatches.Count == 0 ? "ok" : "mismatch", affected, ct)
            .ConfigureAwait(false);

        return new AgentPlanExecutionResult
        {
            Success = true,
            Approved = true,
            PlanDigest = digest,
            Steps = results,
            Verified = verified,
            Mismatches = mismatches,
            AffectedCount = affected
        };
    }

    private async Task<AgentPlanExecutionResult?> RunPreflightAsync(
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results,
        CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            var result = results.First(item => item.StepId == step.StepId);
            if (!_registry.TryGet(step.ToolName, out var tool) || tool is null)
                return AgentPlanExecutionResult.Fail(
                    "plan_tool_unknown", "Unknown tool '" + step.ToolName + "'.");

            if (tool is not IAgentToolPreflight preflight)
            {
                result.PreflightSkipped = true;
                continue;
            }

            JsonElement args;
            try
            {
                using var document = JsonDocument.Parse(step.ArgumentsJson);
                args = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            var context = new AgentToolContext(
                _revit, _session.DocumentId, _session.ConversationId);
            var check = await preflight.PreflightAsync(context, args, ct).ConfigureAwait(false);
            if (check.Ok)
                continue;

            return AgentPlanExecutionResult.Fail(
                check.ErrorCode ?? "plan_preflight_failed",
                "Step '" + step.StepId + "' preflight failed: " + check.Message);
        }

        return null;
    }

    private async Task<AgentPlanExecutionResult?> RunDryRunAsync(
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results,
        CancellationToken ct)
    {
        foreach (var step in steps)
        {
            if (!IsWrite(step.ToolName))
                continue;

            var tool = GetTool(step.ToolName);
            if (tool is null || !tool.Definition.SupportsDryRun)
                continue;

            var result = results.First(item => item.StepId == step.StepId);
            var dryResult = await ExecuteToolAsync(tool, WithDryRun(step.ArgumentsJson, true), true, ct)
                .ConfigureAwait(false);
            result.DryRunResult = dryResult;
            result.DryRunOk = dryResult.Success;
            ReadCounts(dryResult, result);

            if (!dryResult.Success)
            {
                return AgentPlanExecutionResult.Fail(
                    "plan_dry_run_failed",
                    "Step '" + step.StepId + "' dry run failed: " +
                    (dryResult.ErrorCode ?? "unknown"));
            }

            if (step.ExpectedChanges.HasValue)
            {
                result.FidelityChecked = true;
                result.FidelityMatched = result.Changed == step.ExpectedChanges.Value;
                if (!result.FidelityMatched)
                {
                    await WriteAuditAsync(
                            null, "plan#fidelity", "mismatch", result.Changed, ct)
                        .ConfigureAwait(false);
                    return AgentPlanExecutionResult.Fail(
                        "plan_fidelity_mismatch",
                        "Step '" + step.StepId + "' promised " + step.ExpectedChanges.Value +
                        " change(s), the dry run produced " + result.Changed + ".");
                }
            }
        }

        await WriteAuditAsync(null, "plan#fidelity", "match", 0, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<AgentPlanExecutionResult?> RequestApprovalAsync(
        AgentPlan plan,
        string digest,
        int affected,
        CancellationToken ct)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var expiresAt = DateTime.UtcNow + _approvalLifetime;
        var request = new AgentConfirmationRequest
        {
            ToolName = "plan",
            Operation = "plan",
            Summary = plan.Goal,
            Digest = digest,
            PlanDigest = digest,
            Nonce = nonce,
            ExpiresAt = expiresAt,
            ArgumentsJson = DescribePlan(plan),
            AffectedCount = affected,
            Timeout = _approvalLifetime
        };

        var decision = await _confirmation.RequestAsync(request, ct).ConfigureAwait(false);
        if (decision != AgentConfirmationResult.Approved)
            return AgentPlanExecutionResult.Fail(
                "plan_rejected",
                "The plan was not approved (" + decision + "). Nothing was changed.");

        if (DateTime.UtcNow > expiresAt)
            return AgentPlanExecutionResult.Fail(
                "confirmation_expired", "The plan approval expired before apply.");

        if (!_ledger.TryConsume(nonce))
            return AgentPlanExecutionResult.Fail(
                "confirmation_replayed", "This plan approval was already used.");

        if (!string.Equals(ComputePlanDigest(plan), digest, StringComparison.Ordinal))
            return AgentPlanExecutionResult.Fail(
                "confirmation_digest_mismatch",
                "The plan changed after approval; a new confirmaton is required.");

        return null;
    }

    private async Task<AgentPlanExecutionResult?> RunApplyAsync(
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results,
        CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            var tool = GetTool(step.ToolName);
            if (tool is null)
                return AgentPlanExecutionResult.Fail(
                    "plan_tool_unknown", "Unknown tool '" + step.ToolName + "'.");

            var result = results.First(item => item.StepId == step.StepId);
            if (!IsWrite(step.ToolName))
            {
                result.ApplyResult = await ExecuteToolAsync(
                        tool, step.ArgumentsJson, false, ct)
                    .ConfigureAwait(false);
                result.DryRunOk = result.ApplyResult.Success;
                continue;
            }

            var applyResult = await ExecuteToolAsync(
                    tool, WithDryRun(step.ArgumentsJson, false), true, ct)
                .ConfigureAwait(false);
            result.ApplyResult = applyResult;
            ReadCounts(applyResult, result);

            if (!applyResult.Success)
            {
                return AgentPlanExecutionResult.Fail(
                    "plan_apply_failed",
                    "Step '" + step.StepId + "' failed: " +
                    (applyResult.ErrorCode ?? "unknown") + " " + applyResult.Message);
            }
        }

        return null;
    }

    private async Task<(IReadOnlyList<string> Verified, IReadOnlyList<string> Mismatches)>
        RunReadBackAsync(
            IReadOnlyList<AgentPlanStep> steps,
            IReadOnlyList<AgentPlanStepResult> results,
            CancellationToken ct)
    {
        var verified = new List<string>();
        var mismatches = new List<string>();

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.ReadBackTool))
                continue;

            var tool = GetTool(step.ReadBackTool);
            if (tool is null)
            {
                mismatches.Add(step.StepId + ": read-back tool '" + step.ReadBackTool +
                               "' is not registered");
                continue;
            }

            var result = results.First(item => item.StepId == step.StepId);
            var readBack = await ExecuteToolAsync(
                    tool, step.ArgumentsReadBackJson, false, ct)
                .ConfigureAwait(false);
            result.ReadBackResult = readBack;

            if (!readBack.Success || readBack.Data is not { } data)
            {
                mismatches.Add(step.StepId + ": read-back failed (" +
                               (readBack.ErrorCode ?? "unknown") + ")");
                continue;
            }

            var stepMismatch = false;
            foreach (var expectation in step.ReadBackExpectations)
            {
                var actual = ResolvePath(data, expectation.Path);
                if (actual is null
                    || !string.Equals(actual, expectation.Expected, StringComparison.Ordinal))
                {
                    stepMismatch = true;
                    mismatches.Add(step.StepId + ": " + expectation.Path + " expected '" +
                                   expectation.Expected + "', actual '" + (actual ?? "null") + "'");
                }
                else
                {
                    verified.Add(step.StepId + ": " + expectation.Path + " = " + actual);
                }
            }

            if (!stepMismatch && step.ReadBackExpectations.Count == 0)
                verified.Add(step.StepId + ": read-back executed");
        }

        return (verified, mismatches);
    }

    private bool IsWrite(string toolName) =>
        _registry.TryGet(toolName, out var tool)
        && tool is not null
        && tool.Definition.Risk != AgentToolRisk.Read;

    private IAgentTool? GetTool(string name) =>
        _registry.TryGet(name, out var tool) ? tool : null;

    private async Task<ToolResult> ExecuteToolAsync(
        IAgentTool tool,
        string argumentsJson,
        bool approved,
        CancellationToken ct)
    {
        JsonElement args;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            args = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return ToolResult.Fail("invalid_arguments", "Arguments are not valid JSON: " + ex.Message);
        }

        var context = new AgentToolContext(_revit, _session.DocumentId, _session.ConversationId)
        {
            WriteApproved = approved
        };

        try
        {
            return await tool.ExecuteAsync(context, args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent plan step '" + tool.Definition.Name + "' threw: " + ex.GetType().Name +
                ": " + ex.Message + " [Action: the plan stopped; already applied steps can be " +
                "reverted with undo_last]");
            return ToolResult.Fail("tool_exception", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string WithDryRun(string argumentsJson, bool dryRun)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        }
        catch (JsonException)
        {
            return argumentsJson;
        }

        if (node is not JsonObject obj)
            return argumentsJson;

        obj["dryRun"] = dryRun;
        return obj.ToJsonString();
    }

    private static void ReadCounts(ToolResult result, AgentPlanStepResult step)
    {
        if (result.Data is not { ValueKind: JsonValueKind.Object } data)
            return;

        step.Changed = ReadArrayLength(data, "changed");
        step.Skipped = ReadArrayLength(data, "skipped");
        step.Failed = ReadArrayLength(data, "failed");
        if (step.Changed == 0 && data.TryGetProperty("affected", out var affected)
            && affected.ValueKind == JsonValueKind.Number
            && affected.TryGetInt32(out var parsed))
        {
            step.Changed = parsed;
        }
    }

    private static int ReadArrayLength(JsonElement data, string property)
    {
        return data.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
    }

    private static string? ResolvePath(JsonElement element, string path)
    {
        var current = element;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind == JsonValueKind.Object
                && current.TryGetProperty(segment, out var property))
            {
                current = property;
                continue;
            }

            if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var index)
                && index >= 0
                && index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }

            return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => current.GetRawText()
        };
    }

    private static string ComputePlanDigest(AgentPlan plan)
    {
        var json = JsonSerializer.Serialize(new
        {
            plan.PlanId,
            plan.Goal,
            plan.AllowDestructive,
            steps = plan.Steps.Select(step => new
            {
                step.StepId,
                step.ToolName,
                step.ArgumentsJson,
                step.DependsOn,
                step.DryRun,
                step.ExpectedChanges,
                step.ReadBackTool,
                step.ArgumentsReadBackJson
            })
        });

        return AgentActionDigest.Compute("agent_plan", json, string.Empty);
    }

    private static string DescribePlan(AgentPlan plan)
    {
        var steps = plan.Steps.Select(step => new
        {
            stepId = step.StepId,
            tool = step.ToolName,
            expectedChanges = step.ExpectedChanges,
            rationale = step.Rationale
        });
        return JsonSerializer.Serialize(new { plan.Goal, steps });
    }

    private async Task WriteAuditAsync(
        AgentPlan? plan,
        string tool,
        string result,
        int affected,
        CancellationToken ct)
    {
        if (_audit is null)
            return;

        try
        {
            await _audit.WriteAsync(new AgentAuditEntry
            {
                Actor = Environment.UserName,
                DocumentPath = _session.DocumentId,
                Tool = tool,
                ArgsHash = plan is null
                    ? string.Empty
                    : ComputePlanDigest(plan).Substring(0, 16),
                Result = result,
                AffectedCount = affected
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent plan audit write failed: " + ex.Message +
                " [Action: check write access to agent.db; the plan result is not affected]");
        }
    }
}
