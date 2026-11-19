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

            var tool = GetTool(step.ToolName);
            if (tool is null || !tool.Definition.SupportsDryRun)
                continue;

            var result = results.First(item => item.StepId == step.StepId);

            // Every dry run is rolled back, so a step that consumes the output of
            // an earlier write step cannot be previewed honestly. It is reported
            // as preview_skipped and executed for real during apply.
            if (DependsOnWrite(step, byId))
            {
                result.PreviewSkipped = true;
                result.PreviewSkipReason = "depends_on_unapplied_step";
                continue;
            }

            var dryResult = await ExecuteToolAsync(tool, WithDryRun(step.ArgumentsJson, true), true, execution, ct)
                .ConfigureAwait(false);
            result.DryRunResult = dryResult;
            result.DryRunOk = dryResult.Success;
            ReadCounts(dryResult, result);

            if (!dryResult.Success)
            {
                return AgentPlanExecutionResult.Fail(
                    "plan_dry_run_failed",
                    "Step '" + step.StepId + "' dry run failed: " +
                    (dryResult.ErrorCode ?? "unknown") +
                    ". Full per-step diff is in steps[].",
                    results);
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
                        ", the dry run produced " + result.Changed);
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

    private async Task<AgentPlanExecutionResult?> RunApplyAsync(
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results,
        AgentPlanExecutionContext execution,
        CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            var tool = GetTool(step.ToolName);
            if (tool is null)
                return AgentPlanExecutionResult.Fail(
                    "plan_tool_unknown", "Unknown tool '" + step.ToolName + "'.", results);

            var result = results.First(item => item.StepId == step.StepId);
            if (!IsWrite(step.ToolName))
            {
                result.ApplyResult = await ExecuteToolAsync(
                        tool, step.ArgumentsJson, false, execution, ct)
                    .ConfigureAwait(false);
                result.DryRunOk = result.ApplyResult.Success;
                if (!result.ApplyResult.Success)
                {
                    return AgentPlanExecutionResult.Fail(
                        "plan_read_failed",
                        "Step '" + step.StepId + "' (read) failed: " +
                        (result.ApplyResult.ErrorCode ?? "unknown") + " " +
                        result.ApplyResult.Message,
                        results);
                }

                continue;
            }

            var applyResult = await ExecuteToolAsync(
                    tool, WithDryRun(step.ArgumentsJson, false), true, execution, ct)
                .ConfigureAwait(false);
            result.ApplyResult = applyResult;
            ReadCounts(applyResult, result);
            CollectFamilyAliases(applyResult, execution.FamilyIdAliases);

            if (!applyResult.Success)
            {
                return AgentPlanExecutionResult.Fail(
                    "plan_apply_failed",
                    "Step '" + step.StepId + "' failed: " +
                    (applyResult.ErrorCode ?? "unknown") + " " + applyResult.Message,
                    results);
            }
        }

        return null;
    }

    private async Task<(IReadOnlyList<string> Verified, IReadOnlyList<string> Mismatches)>
        RunReadBackAsync(
            IReadOnlyList<AgentPlanStep> steps,
            IReadOnlyList<AgentPlanStepResult> results,
            AgentPlanExecutionContext execution,
            CancellationToken ct)
    {
        var verified = new List<string>();
        var mismatches = new List<string>();

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(step.ReadBackTool))
                continue;

            var readBackToolName = step.ReadBackTool!;
            var tool = GetTool(readBackToolName);
            if (tool is null)
            {
                mismatches.Add(step.StepId + ": read-back tool '" + readBackToolName +
                               "' is not registered");
                continue;
            }

            var result = results.First(item => item.StepId == step.StepId);
            var readBack = await ExecuteToolAsync(
                    tool, step.ArgumentsReadBackJson, false, execution, ct)
                .ConfigureAwait(false);
            result.ReadBackResult = readBack;
            result.ReadBackExecuted = true;

            if (!readBack.Success || readBack.Data is not { } data)
            {
                mismatches.Add(step.StepId + ": read-back failed (" +
                               (readBack.ErrorCode ?? "unknown") + ")");
                continue;
            }

            var stepMismatch = false;
            result.ExpectationsChecked = step.ReadBackExpectations.Count;
            foreach (var expectation in step.ReadBackExpectations)
            {
                var actual = ResolvePath(data, expectation.Path);
                if (actual is null
                    || !string.Equals(actual, expectation.Expected, StringComparison.Ordinal))
                {
                    stepMismatch = true;
                    result.MismatchCount++;
                    mismatches.Add(step.StepId + ": " + expectation.Path + " expected '" +
                                   expectation.Expected + "', actual '" + (actual ?? "null") + "'");
                }
                else
                {
                    result.VerifiedCount++;
                    verified.Add(step.StepId + ": " + expectation.Path + " = " + actual);
                }
            }

            if (!stepMismatch && step.ReadBackExpectations.Count == 0)
                verified.Add(step.StepId + ": read-back executed");
        }

        return (verified, mismatches);
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
