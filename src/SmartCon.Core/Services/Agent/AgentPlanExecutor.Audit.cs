using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    private static string ComputePlanDigest(AgentPlan plan, string? documentId)
    {
        // The digest covers the executable intent only. plan.Goal is free-form prose:
        // rewording it between preview and apply must not invalidate an approved plan
        // (benchmark run 2, task 5: plan_digest_mismatch forced 50 single calls).
        var json = JsonSerializer.Serialize(new
        {
            plan.AllowDestructive,
            steps = plan.Steps.Select(step => new
            {
                step.StepId,
                step.ToolName,
                ArgumentsJson = CanonicalizeWithoutDryRun(step.ArgumentsJson),
                DependsOn = step.DependsOn.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                step.ExpectedChanges,
                step.ExpectedChangesAtLeast
                // Read-back fields are deliberately excluded: they do not change
                // the model, so adding them after the preview must not invalidate
                // the approved digest (stress test #2).
            })
        });

        return AgentActionDigest.Compute("agent_plan", json, documentId ?? string.Empty);
    }

    /// <summary>
    /// Canonical form of the arguments used for the plan digest. The dry-run flag
    /// is a transport/service flag, not part of what the user approves: the
    /// preview runs with dryRun=true and the apply flips it to false, so it must
    /// not change the digest (live dogfood P0-1).
    /// </summary>
    private static string CanonicalizeWithoutDryRun(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json!) is not System.Text.Json.Nodes.JsonObject obj)
                return AgentActionDigest.CanonicalizeJson(json!);

            obj.Remove("dryRun");
            return AgentActionDigest.CanonicalizeJson(obj.ToJsonString());
        }
        catch (JsonException)
        {
            return json!;
        }
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
            expectedChangesAtLeast = step.ExpectedChangesAtLeast,
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
