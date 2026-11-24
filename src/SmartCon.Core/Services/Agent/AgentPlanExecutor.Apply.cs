using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    private async Task<AgentPlanExecutionResult?> RunApplyAsync(
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results,
        AgentPlanExecutionContext execution,
        CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            var result = results.First(item => item.StepId == step.StepId);
            if (result.ApplyResult is not null)
            {
                // Already marked upstream_step_failed by an earlier failed
                // dependency: nothing to run.
                continue;
            }

            var tool = GetTool(step.ToolName);
            if (tool is null)
                return AgentPlanExecutionResult.Fail(
                    "plan_tool_unknown", "Unknown tool '" + step.ToolName + "'.", results);

            // {"$step": …} references resolve here against the real apply
            // results, so a step can configure what an earlier step created
            // without re-reading the id (S.42).
            var resolved = ResolveStepArguments(step, results);
            if (resolved.Failure is not null)
            {
                result.ApplyResult = resolved.Failure;
                ReadCounts(resolved.Failure, result);
                var failure = FailStep(
                    step, result, steps, results,
                    "plan_apply_failed",
                    "Step '" + step.StepId + "': " + resolved.Failure.Message,
                    markUpstream: true);
                if (failure is not null)
                    return failure;

                continue;
            }

            var argumentsJson = resolved.ArgumentsJson;
            var usesReferences = ContainsStepReference(step.ArgumentsJson)
                                 || ContainsStepReference(step.ArgumentsReadBackJson);

            if (!IsWrite(step.ToolName))
            {
                result.ApplyResult = await ExecuteToolAsync(
                        tool, argumentsJson, false, execution, ct)
                    .ConfigureAwait(false);
                result.DryRunOk = result.ApplyResult.Success;
                if (!result.ApplyResult.Success)
                {
                    var failure = FailStep(
                        step, result, steps, results,
                        "plan_read_failed",
                        "Step '" + step.StepId + "' (read) failed: " +
                        (result.ApplyResult.ErrorCode ?? "unknown") + " " +
                        (result.ApplyResult.Message ?? string.Empty),
                        markUpstream: true);
                    if (failure is not null)
                        return failure;
                }

                continue;
            }

            // Reference-carrying steps skip the L0 schema check (the reference is
            // not the final value), so the resolved arguments are validated here.
            if (usesReferences)
            {
                var schemaErrors = AgentToolSchemaValidator.Validate(
                    tool.Definition.InputSchemaJson, argumentsJson);
                if (schemaErrors.Count > 0)
                {
                    result.ApplyResult = ToolResult.Fail(
                        "plan_step_args_invalid",
                        "Step '" + step.StepId + "' resolved arguments are invalid: " +
                        string.Join("; ", schemaErrors));
                    ReadCounts(result.ApplyResult, result);
                    var failure = FailStep(
                        step, result, steps, results,
                        "plan_apply_failed",
                        result.ApplyResult.Message ?? string.Empty,
                        markUpstream: true);
                    if (failure is not null)
                        return failure;

                    continue;
                }
            }

            var applyResult = await ExecuteToolAsync(
                    tool, WithDryRun(argumentsJson, false), true, execution, ct)
                .ConfigureAwait(false);
            result.ApplyResult = applyResult;
            ReadCounts(applyResult, result);
            CollectFamilyAliases(applyResult, execution.FamilyIdAliases);
            result.ExpectedFailure = result.ExpectedFailure && !applyResult.Success;

            if (!applyResult.Success)
            {
                var failure = FailStep(
                    step, result, steps, results,
                    "plan_apply_failed",
                    "Step '" + step.StepId + "' failed: " +
                    (applyResult.ErrorCode ?? "unknown") + " " + applyResult.Message,
                    markUpstream: true);
                if (failure is not null)
                    return failure;
            }
        }

        return null;
    }

    private static (string ArgumentsJson, ToolResult? Failure) ResolveStepArguments(
        AgentPlanStep step,
        IReadOnlyList<AgentPlanStepResult> results)
    {
        if (!ContainsStepReference(step.ArgumentsJson))
            return (step.ArgumentsJson, null);

        var byId = results.ToDictionary(item => item.StepId, StringComparer.Ordinal);
        var resolved = TryResolveStepReferences(step.ArgumentsJson, byId, out var error);
        if (error is not null)
        {
            return (step.ArgumentsJson, ToolResult.Fail(
                "step_reference_unresolved",
                "the {" + StepReferenceKey + "} reference cannot be resolved: " + error +
                ". [Action: check the stepId and the dot path, or re-read the created id " +
                "with the matching read tool]"));
        }

        return (resolved ?? step.ArgumentsJson, null);
    }

    /// <summary>
    /// A failed apply step stops the plan, so the steps that wanted the id it was
    /// supposed to create never run. They get a typed upstream_step_failed result
    /// instead of a bare not_run, so the model sees which dependency broke.
    /// </summary>
    private static void MarkUpstreamFailures(
        AgentPlanStep failedStep,
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results)
    {
        var byId = steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var result = results.FirstOrDefault(item => item.StepId == step.StepId);
            if (result is null || result.ApplyResult is not null)
                continue;

            var visiting = new HashSet<string>(StringComparer.Ordinal);
            if (!DependsOnCore(step, failedStep.StepId, byId, visiting))
                continue;

            result.ApplyResult = ToolResult.Fail(
                "upstream_step_failed",
                "Step '" + step.StepId + "' depends on failed step '" + failedStep.StepId +
                "' (tool " + failedStep.ToolName + ") and was not run.");
        }
    }

    private static bool DependsOnCore(
        AgentPlanStep step,
        string stepId,
        IReadOnlyDictionary<string, AgentPlanStep> byId,
        ISet<string> visiting)
    {
        foreach (var dependency in step.DependsOn)
        {
            if (string.Equals(dependency, stepId, StringComparison.Ordinal))
                return true;

            if (!visiting.Add(dependency))
                continue;

            if (byId.TryGetValue(dependency, out var dependentStep)
                && DependsOnCore(dependentStep, stepId, byId, visiting))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One failure path for every step failure: dependents of an apply failure
    /// get a typed upstream_step_failed, and a step with onError="continue"
    /// records the failure as expected instead of stopping the plan (S.42,
    /// negative guard checks). Returns null when the plan continues.
    /// </summary>
    private static AgentPlanExecutionResult? FailStep(
        AgentPlanStep step,
        AgentPlanStepResult result,
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results,
        string code,
        string message,
        bool markUpstream)
    {
        if (markUpstream)
            MarkUpstreamFailures(step, steps, results);

        if (ContinuesOnError(step))
        {
            result.ExpectedFailure = true;
            return null;
        }

        return AgentPlanExecutionResult.Fail(code, message, results);
    }

    private static bool ContinuesOnError(AgentPlanStep step) =>
        string.Equals(step.OnError, "continue", StringComparison.OrdinalIgnoreCase);
}
