using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// L0 plan contract: tool names exist, arguments parse and pass the tool schema,
/// step ids are unique, dependencies are known and acyclic, the plan is not too
/// large, and destructive tools need an explicit opt-in. Pure logic — no Revit,
/// no LLM, fully unit-testable.
/// </summary>
public static class AgentPlanValidator
{
    public const int MaxSteps = 20;

    public static AgentPlanValidationResult Validate(
        AgentPlan? plan,
        IReadOnlyList<AgentToolDefinition> tools)
    {
        var errors = new List<AgentPlanIssue>();
        var warnings = new List<AgentPlanIssue>();

        if (plan is null)
        {
            errors.Add(new AgentPlanIssue
            {
                Code = "plan_missing",
                Message = "The plan is missing."
            });
            return new AgentPlanValidationResult { Errors = errors, Warnings = warnings };
        }

        if (string.IsNullOrWhiteSpace(plan.Goal))
        {
            errors.Add(new AgentPlanIssue
            {
                Code = "plan_goal_missing",
                Message = "The plan goal is empty."
            });
        }

        if (plan.Steps.Count == 0)
        {
            errors.Add(new AgentPlanIssue
            {
                Code = "plan_empty",
                Message = "The plan has no steps."
            });
            return new AgentPlanValidationResult { Errors = errors, Warnings = warnings };
        }

        if (plan.Steps.Count > MaxSteps)
        {
            errors.Add(new AgentPlanIssue
            {
                Code = "plan_too_large",
                Message = "The plan has " + plan.Steps.Count + " steps; at most " + MaxSteps +
                          " are allowed."
            });
        }

        var byName = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        var stepIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.StepId))
            {
                errors.Add(new AgentPlanIssue
                {
                    Code = "plan_step_id_missing",
                    Message = "A plan step has no stepId."
                });
                continue;
            }

            if (!stepIds.Add(step.StepId))
            {
                errors.Add(new AgentPlanIssue
                {
                    Code = "plan_duplicate_step",
                    Message = "Duplicate stepId '" + step.StepId + "'.",
                    StepId = step.StepId
                });
                continue;
            }

            if (!byName.TryGetValue(step.ToolName, out var tool))
            {
                errors.Add(new AgentPlanIssue
                {
                    Code = "plan_tool_unknown",
                    Message = "Unknown tool '" + step.ToolName + "'.",
                    StepId = step.StepId
                });
                continue;
            }

            if (string.Equals(step.ToolName, "execute_plan", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new AgentPlanIssue
                {
                    Code = "plan_nested_plan_forbidden",
                    Message = "A plan step cannot be execute_plan itself.",
                    StepId = step.StepId
                });
                continue;
            }

            if (tool.Risk == AgentToolRisk.Destructive && !plan.AllowDestructive)
            {
                errors.Add(new AgentPlanIssue
                {
                    Code = "plan_destructive_forbidden",
                    Message = "Tool '" + tool.Name + "' is destructive; the plan must set " +
                              "AllowDestructive explicitly.",
                    StepId = step.StepId
                });
            }

            var schemaErrors = AgentToolSchemaValidator.Validate(
                tool.InputSchemaJson, step.ArgumentsJson);
            if (schemaErrors.Count > 0)
            {
                errors.Add(new AgentPlanIssue
                {
                    Code = "plan_args_invalid",
                    Message = "Arguments of '" + step.ToolName + "' are invalid: " +
                              string.Join("; ", schemaErrors),
                    StepId = step.StepId
                });
            }

            if (step.ExpectedChanges.HasValue && step.ExpectedChangesAtLeast.HasValue)
            {
                errors.Add(new AgentPlanIssue
                {
                    Code = "plan_expected_changes_conflict",
                    Message = "Step '" + step.StepId + "' sets both ExpectedChanges and " +
                              "ExpectedChangesAtLeast; use one.",
                    StepId = step.StepId
                });
            }

            if (!step.DryRun)
            {
                warnings.Add(new AgentPlanIssue
                {
                    Code = "plan_step_apply_in_dry_phase",
                    Message = "Step '" + step.StepId + "' requests DryRun=false; the plan " +
                              "phase always runs dry first.",
                    StepId = step.StepId
                });
            }

            if (tool.Risk != AgentToolRisk.Read
                && step.ExpectedChanges is null
                && step.ExpectedChangesAtLeast is null)
            {
                warnings.Add(new AgentPlanIssue
                {
                    Code = "plan_no_expected_changes",
                    Message = "Write step '" + step.StepId + "' has no ExpectedChanges, so " +
                              "dry-run fidelity cannot be checked.",
                    StepId = step.StepId
                });
            }

            if (tool.Risk != AgentToolRisk.Read && string.IsNullOrWhiteSpace(step.ReadBackTool))
            {
                warnings.Add(new AgentPlanIssue
                {
                    Code = "plan_no_readback",
                    Message = "Write step '" + step.StepId + "' has no ReadBackTool; the " +
                              "applied change will not be verified.",
                    StepId = step.StepId
                });
            }
        }

        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.StepId))
                continue;

            foreach (var dependency in step.DependsOn)
            {
                if (string.Equals(dependency, step.StepId, StringComparison.Ordinal))
                {
                    errors.Add(new AgentPlanIssue
                    {
                        Code = "plan_cycle",
                        Message = "Step '" + step.StepId + "' depends on itself.",
                        StepId = step.StepId
                    });
                    continue;
                }

                if (!stepIds.Contains(dependency))
                {
                    errors.Add(new AgentPlanIssue
                    {
                        Code = "plan_dependency_unknown",
                        Message = "Step '" + step.StepId + "' depends on unknown step '" +
                                  dependency + "'.",
                        StepId = step.StepId
                    });
                }
            }
        }

        if (errors.All(issue => issue.Code != "plan_cycle"))
        {
            var cycle = FindCycle(plan);
            if (cycle is not null)
            {
                errors.Add(new AgentPlanIssue
                {
                    Code = "plan_cycle",
                    Message = "The step graph contains a cycle: " + string.Join(" → ", cycle),
                    StepId = cycle.FirstOrDefault()
                });
            }
        }

        return new AgentPlanValidationResult { Errors = errors, Warnings = warnings };
    }

    /// <summary>Topological order of step ids (dependencies first). Assumes a valid plan.</summary>
    public static IReadOnlyList<string> OrderSteps(AgentPlan plan)
    {
        var byId = plan.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.StepId))
            .GroupBy(step => step.StepId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var result = new List<string>(byId.Count);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in byId.Keys)
            Visit(id);

        return result;

        void Visit(string id)
        {
            if (visited.Contains(id) || !byId.TryGetValue(id, out var step))
                return;

            if (!visiting.Add(id))
                return;

            foreach (var dependency in step.DependsOn)
                Visit(dependency);

            visiting.Remove(id);
            if (visited.Add(id))
                result.Add(id);
        }
    }

    private static List<string>? FindCycle(AgentPlan plan)
    {
        var byId = plan.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.StepId))
            .GroupBy(step => step.StepId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var id in byId.Keys)
        {
            var cycle = Visit(id);
            if (cycle is not null)
                return cycle;
        }

        return null;

        List<string>? Visit(string id)
        {
            if (!byId.TryGetValue(id, out var step))
                return null;

            if (state.TryGetValue(id, out var mark))
            {
                if (mark == 1)
                {
                    var start = path.IndexOf(id);
                    var cycle = start >= 0 ? path.Skip(start).ToList() : new List<string>();
                    cycle.Add(id);
                    return cycle;
                }

                return null;
            }

            state[id] = 1;
            path.Add(id);
            foreach (var dependency in step.DependsOn)
            {
                var cycle = Visit(dependency);
                if (cycle is not null)
                    return cycle;
            }

            path.RemoveAt(path.Count - 1);
            state[id] = 2;
            return null;
        }
    }
}
