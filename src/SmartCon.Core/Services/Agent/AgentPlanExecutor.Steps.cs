using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    private async Task<AgentPlanExecutionResult?> RunPreflightAsync(
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results,
        AgentPlanExecutionContext execution,
        CancellationToken ct)
    {
        var byId = steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);
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
                _revit, execution.DocumentId, execution.ConversationId);
            var check = await preflight.PreflightAsync(context, args, ct).ConfigureAwait(false);
            if (check.Ok)
                continue;

            // A step may reference the id of an element that an earlier write
            // step creates. The dry run cannot commit that element, so preflight
            // is deferred to apply instead of rejecting a valid plan.
            if (string.Equals(check.ErrorCode, "unresolved_id", StringComparison.Ordinal)
                && DependsOnWrite(step, byId))
            {
                result.PreflightDeferred = true;
                continue;
            }

            return AgentPlanExecutionResult.Fail(
                check.ErrorCode ?? "plan_preflight_failed",
                "Step '" + step.StepId + "' preflight failed: " + check.Message);
        }

        return null;
    }

    private async Task<AgentPlanExecutionResult?> RunDryRunAsync(
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results,
        AgentPlanExecutionContext execution,
        CancellationToken ct)
    {
        var mismatches = new List<string>();
        var byId = steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsWrite(step.ToolName))
                continue;

            var result = results.First(item => item.StepId == step.StepId);
            var tool = GetTool(step.ToolName);
            if (tool is null)
            {
                result.PreviewSkipped = true;
                result.PreviewSkipReason = "unknown_tool";
                continue;
            }

            if (!tool.Definition.SupportsDryRun)
            {
                // The step will work for real during apply. Claiming a complete
                // preview while it was never dry-run is exactly what made a
                // destructive retry look safe on approval.
                result.PreviewSkipped = true;
                result.PreviewSkipReason = "tool_has_no_dry_run";
                continue;
            }

            // Every dry run is rolled back, so a step that consumes the output of
            // an earlier write step cannot be previewed honestly. It is reported
            // as preview_skipped (with the blocking step) and executed for real
            // during apply.
            if (DependsOnWrite(step, byId))
            {
                var blocking = FirstWriteDependency(step, byId);
                result.PreviewSkipped = true;
                result.PreviewSkipReason = "depends_on_unapplied_step:" +
                                           (blocking is null ? "?" : blocking.StepId) +
                                           "(" + (blocking?.ToolName ?? "?") + ")";
                continue;
            }

            // A {"$step": …} reference resolves from the referenced step's APPLY
            // result; the dry run rolled that step back, so its created object
            // does not exist here either — same skip, same honest reason.
            if (ContainsStepReference(step.ArgumentsJson)
                || ContainsStepReference(step.ArgumentsReadBackJson))
            {
                var target = ReferencedStepIds(step.ArgumentsJson)
                    .Concat(ReferencedStepIds(step.ArgumentsReadBackJson))
                    .FirstOrDefault() ?? "?";
                result.PreviewSkipped = true;
                result.PreviewSkipReason = "uses_step_reference:" + target;
                continue;
            }

            var dryResult = await ExecuteToolAsync(tool, WithDryRun(step.ArgumentsJson, true), true, execution, ct)
                .ConfigureAwait(false);
            result.DryRunResult = dryResult;
            result.DryRunOk = dryResult.Success;
            ReadCounts(dryResult, result);

            if (!dryResult.Success)
            {
                // A negative guard check inside the plan (onError="continue")
                // records the failure and lets the plan go on (S.42).
                var failure = FailStep(
                    step, result, steps, results,
                    "plan_dry_run_failed",
                    "Step '" + step.StepId + "' dry run failed: " +
                    (dryResult.ErrorCode ?? "unknown") +
                    ". Full per-step diff is in steps[].",
                    markUpstream: false);
                if (failure is not null)
                    return failure;

                continue;
            }

            if (step.ExpectedChanges.HasValue || step.ExpectedChangesAtLeast.HasValue)
            {
                result.FidelityChecked = true;
                result.FidelityExpected = step.ExpectedChanges ?? step.ExpectedChangesAtLeast;

                // A step that changed nothing because every target already had the
                // requested state (skipped, no failures) keeps the promise: treating
                // it as a mismatch forced the model to rewrite expectations by hand
                // (benchmark run 2, task 1).
                var noOpClean = result.Changed == 0 && result.Skipped > 0 && result.Failed == 0;
                result.FidelityMatched = noOpClean
                    || (step.ExpectedChanges.HasValue
                        ? result.Changed == step.ExpectedChanges.Value
                        : result.Changed >= step.ExpectedChangesAtLeast!.Value);
                if (noOpClean)
                {
                    result.FidelityNote = "no-op: every target already had the requested " +
                                          "state (" + result.Skipped + " skipped)";
                }

                if (!result.FidelityMatched)
                {
                    await WriteAuditAsync(
                            null, "plan#fidelity", "mismatch", result.Changed, execution, ct)
                        .ConfigureAwait(false);
                    mismatches.Add(
                        "Step '" + step.StepId + "' promised " +
                        (step.ExpectedChanges.HasValue
                            ? step.ExpectedChanges.Value + " change(s)"
                            : "at least " + step.ExpectedChangesAtLeast!.Value + " change(s)") +
                        ", the tool counted " + result.Changed + " change(s)" +
                        DescribeCountedItems(dryResult) +
                        ". Use steps[].changed of this preview as expectedChanges: it is the " +
                        "tool's own change count (in the report items.changed[] may be shorter " +
                        "by items.duplicatesInCreated)");
                }
            }
        }

        if (mismatches.Count > 0)
        {
            // Continue the dry run through every step so the model gets the full
            // per-step diff even when one step misses its estimate (stress #2).
            return AgentPlanExecutionResult.Fail(
                "plan_fidelity_mismatch",
                string.Join("; ", mismatches) +
                ". Full per-step diff is in steps[]; fix the expectedChanges or the step " +
                "arguments and preview again.",
                results);
        }

        await WriteAuditAsync(null, "plan#fidelity", "match", 0, execution, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<AgentPlanExecutionResult?> RequestApprovalAsync(
        AgentPlan plan,
        string digest,
        int affected,
        AgentPlanExecutionContext execution,
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

        if (execution.ApprovalMode == AgentApprovalMode.AutoApproveSession
            && !PlanContainsDiscard(plan))
        {
            if (!_ledger.TryConsume(nonce))
                return AgentPlanExecutionResult.Fail(
                    "confirmation_replayed", "This plan approval was already used.");

            if (!string.Equals(ComputePlanDigest(plan, execution.DocumentId), digest, StringComparison.Ordinal))
                return AgentPlanExecutionResult.Fail(
                    "confirmation_digest_mismatch",
                    "The plan changed after the dry run; a new confirmation is required.");

            await WriteAuditAsync(
                    plan, "plan#approval", "auto_approved", affected, execution, ct)
                .ConfigureAwait(false);
            return null;
        }

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

        if (!string.Equals(ComputePlanDigest(plan, execution.DocumentId), digest, StringComparison.Ordinal))
            return AgentPlanExecutionResult.Fail(
                "confirmation_digest_mismatch",
                "The plan changed after approval; a new confirmaton is required.");

        return null;
    }

    private static bool PlanContainsDiscard(AgentPlan plan) =>
        plan.Steps.Any(step =>
            string.Equals(step.ToolName, "discard_change_set", StringComparison.OrdinalIgnoreCase));

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
        AgentPlanExecutionContext execution,
        CancellationToken ct)
    {
        JsonElement args;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : ApplyFamilyAliases(argumentsJson, execution.FamilyIdAliases));
            args = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return ToolResult.Fail("invalid_arguments", "Arguments are not valid JSON: " + ex.Message);
        }

        var context = new AgentToolContext(_revit, execution.DocumentId, execution.ConversationId)
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
}
