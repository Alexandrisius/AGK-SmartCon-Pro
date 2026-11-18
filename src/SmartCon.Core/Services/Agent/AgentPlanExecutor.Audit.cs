using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    private static string ComputePlanDigest(AgentPlan plan, string? documentId)
    {
        var json = JsonSerializer.Serialize(new
        {
            plan.Goal,
            plan.AllowDestructive,
            steps = plan.Steps.Select(step => new
            {
                step.StepId,
                step.ToolName,
                ArgumentsJson = Canonicalize(step.ArgumentsJson),
                DependsOn = step.DependsOn.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                step.DryRun,
                step.ExpectedChanges,
                step.ReadBackTool,
                ArgumentsReadBackJson = Canonicalize(step.ArgumentsReadBackJson),
                readBackExpectations = step.ReadBackExpectations
                    .OrderBy(expectation => expectation.Path, StringComparer.Ordinal)
                    .ThenBy(expectation => expectation.Expected, StringComparer.Ordinal)
                    .Select(expectation => new
                    {
                        expectation.Path,
                        expectation.Expected
                    })
            })
        });

        return AgentActionDigest.Compute("agent_plan", json, documentId ?? string.Empty);
    }

    /// <summary>Whitespace/key-order-insensitive form of a step's JSON arguments.</summary>
    private static string Canonicalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        try
        {
            return AgentActionDigest.CanonicalizeJson(json!);
        }
        catch (JsonException)
        {
            return json!;
        }
    }

    private static string DescribePlan(AgentPlan plan)
    {
        var steps = plan.Steps.Select(step => new
        {
            stepId = step.StepId,
            tool = step.ToolName,
            arguments = step.ArgumentsJson,
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
        AgentPlanExecutionContext execution,
        CancellationToken ct)
    {
        if (_audit is null)
            return;

        try
        {
            await _audit.WriteAsync(new AgentAuditEntry
            {
                Actor = Environment.UserName,
                DocumentPath = execution.DocumentId,
                Tool = tool,
                ArgsHash = plan is null
                    ? string.Empty
                    : ComputePlanDigest(plan, execution.DocumentId).Substring(0, 16),
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
