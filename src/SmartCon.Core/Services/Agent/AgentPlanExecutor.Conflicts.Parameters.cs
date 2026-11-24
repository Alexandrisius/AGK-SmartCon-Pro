using System.Globalization;
using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    /// <summary>
    /// The view parameters a template controls (normalized names), learned from
    /// the dry-run delta of apply_view_template — a later set_view_parameters
    /// writing one of them is guaranteed to fail with
    /// view_controlled_by_template once the template is applied (S.42).
    /// </summary>
    private sealed class ParameterLock
    {
        public ParameterLock(long templateId, string templateName, string stepId, IReadOnlyList<string> parameters)
        {
            TemplateId = templateId;
            TemplateName = templateName;
            StepId = stepId;
            Parameters = new HashSet<string>(
                parameters.Select(name => NormalizeKey(name)),
                StringComparer.Ordinal);
        }

        public long TemplateId { get; }

        public string TemplateName { get; }

        public string StepId { get; }

        public HashSet<string> Parameters { get; }
    }

    /// <summary>
    /// Cross-step preflight: a template applied by one step takes over crop,
    /// visibility, view range and — when it includes "V/G Overrides Filters" —
    /// the filter list of its views, so a later step that writes one of those
    /// settings is guaranteed to fail with view_controlled_by_template. The
    /// dry-run delta of apply_view_template carries the controlled set (and
    /// templateControlsFilters); set_view_template_inclusion releases the lock
    /// for its section. Filter tools (create_view_filter,
    /// set_filter_overrides) conflict only through the "filters" section: a
    /// template that controls model/annotation graphics but not filters does
    /// not stop them (F1 × WA review).
    /// </summary>
    private static AgentPlanExecutionResult? FindStepConflict(
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results)
    {
        var locks = new Dictionary<(long ViewId, string Section), LockedSetting>();
        var parameterLocks = new Dictionary<long, ParameterLock>();

        foreach (var step in steps)
        {
            var result = results.FirstOrDefault(item => item.StepId == step.StepId);

            if (IsTool(step, "apply_view_template"))
            {
                if (result?.DryRunResult is { Success: true, Data: { } data }
                    && data.ValueKind == JsonValueKind.Object)
                {
                    AddTemplateLocks(data, step, locks);
                    AddTemplateParameterLocks(data, step, parameterLocks);
                }

                continue;
            }

            if (IsTool(step, "set_view_template_inclusion"))
            {
                ReleaseTemplateLocks(step, result, locks);
                ReleaseTemplateParameterLocks(step, parameterLocks);
                continue;
            }

            // A template controls whole parameter groups, not only crop/visibility/
            // viewRange: set_view_parameters writing a parameter the template
            // controls is the exact plan that died at apply after a green preview
            // (S.42, the stamp-section parameter of the applied template).
            if (IsTool(step, "set_view_parameters"))
            {
                var conflict = FindParameterConflict(step, parameterLocks, results);
                if (conflict is not null)
                    return conflict;
            }

            foreach (var target in ReadWriteTargets(step))
            {
                if (!locks.TryGetValue((target.ViewId, target.Section), out var locked))
                    continue;

                var remedy = target.Section switch
                {
                    "crop" =>
                        "Crop cannot be detached from a view template in Revit: remove the " +
                        "template from view " + target.ViewId + " or drop the crop step.",
                    "filters" =>
                        "Either write the filter into the template itself (viewIds=[" +
                        locked.TemplateId + "]) so every view of the template keeps it, or " +
                        "detach 'filters' from the template first — set_view_template_inclusion(" +
                        "templateId=" + locked.TemplateId + ", parameters=[\"filters\"], " +
                        "included=false) — and only then apply per-view filters.",
                    _ =>
                        "Add set_view_template_inclusion(templateId=" + locked.TemplateId +
                        ", parameters=[\"" + target.Section + "\"], included=false) before step '" +
                        step.StepId + "', or remove the conflicting write."
                };

                return AgentPlanExecutionResult.Fail(
                    "plan_step_conflict",
                    "Step '" + step.StepId + "' writes " + target.Section + " on view " +
                    target.ViewId + ", but step '" + locked.StepId + "' applies template '" +
                    locked.TemplateName + "' which controls that setting. " + remedy +
                    " Full per-step diff is in steps[].",
                    results);
            }
        }

        return null;
    }

    private static bool IsTool(AgentPlanStep step, string toolName) =>
        string.Equals(step.ToolName, toolName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the controlled parameter names of every view the template delta
    /// covers: overwritten[] when the dry run carried the full list, else the
    /// shorter overwrittenSample[] — both are names set_view_parameters writes
    /// with (S.42).
    /// </summary>
    private static void AddTemplateParameterLocks(
        JsonElement data,
        AgentPlanStep step,
        IDictionary<long, ParameterLock> locks)
    {
        if (!data.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in delta.EnumerateArray())
        {
            if (!TryGetLong(item, "viewId", out var viewId)
                || !TryGetLong(item, "templateId", out var templateId))
            {
                continue;
            }

            var parameters = ReadNameArray(item, "overwritten");
            if (parameters.Count == 0)
                parameters = ReadNameArray(item, "overwrittenSample");

            if (parameters.Count == 0)
                continue;

            locks[viewId] = new ParameterLock(
                templateId,
                GetString(item, "templateName") ?? "#" + templateId,
                step.StepId,
                parameters);
        }
    }

    private static List<string> ReadNameArray(JsonElement element, string property)
    {
        var names = new List<string>();
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                names.Add(item.GetString()!);
        }

        return names;
    }

    /// <summary>
    /// set_view_template_inclusion(parameters:["<name>"], included=false) detaches
    /// that parameter from the template, so the earlier recommendation stops being
    /// a dead end: without this release the retried plan would hit the same
    /// plan_step_conflict again (S.42). Names come from the step arguments — the
    /// same spellings the model was told to use in the remedy.
    /// </summary>
    private static void ReleaseTemplateParameterLocks(
        AgentPlanStep step,
        IDictionary<long, ParameterLock> parameterLocks)
    {
        if (parameterLocks.Count == 0)
            return;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(step.ArgumentsJson) ? "{}" : step.ArgumentsJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (!TryGetLong(root, "templateId", out var templateId))
            return;

        var defaultIncluded = GetBool(root, "included", false);
        var released = new List<string>();
        if (root.TryGetProperty("parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in parameters.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && !defaultIncluded
                    && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    released.Add(item.GetString()!);
                }
            }
        }

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || GetBool(item, "included", defaultIncluded))
                {
                    continue;
                }

                var name = GetString(item, "parameter");
                // net48 BCL has no [NotNullWhen] on IsNullOrWhiteSpace: the
                // explicit null check keeps the nullable analysis happy.
                if (name is not null && !string.IsNullOrWhiteSpace(name))
                    released.Add(name);
            }
        }

        if (released.Count == 0)
            return;

        // The dictionary is keyed by view id; the release targets every view
        // locked by THIS template.
        var releasedKeys = new List<long>();
        foreach (var pair in parameterLocks)
        {
            if (pair.Value.TemplateId != templateId)
                continue;

            foreach (var name in released)
                pair.Value.Parameters.Remove(NormalizeKey(name));

            if (pair.Value.Parameters.Count == 0)
                releasedKeys.Add(pair.Key);
        }

        foreach (var viewId in releasedKeys)
            parameterLocks.Remove(viewId);
    }

    private static AgentPlanExecutionResult? FindParameterConflict(
        AgentPlanStep step,
        IReadOnlyDictionary<long, ParameterLock> parameterLocks,
        IReadOnlyList<AgentPlanStepResult> results)
    {
        if (parameterLocks.Count == 0)
            return null;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(step.ArgumentsJson) ? "{}" : step.ArgumentsJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (!root.TryGetProperty("parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var viewId in ReadViewIds(root))
        {
            if (!parameterLocks.TryGetValue(viewId, out var lock_))
                continue;

            foreach (var parameter in parameters.EnumerateArray())
            {
                if (parameter.ValueKind != JsonValueKind.Object)
                    continue;

                var name = GetString(parameter, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!lock_.Parameters.Contains(NormalizeKey(name)))
                    continue;

                return AgentPlanExecutionResult.Fail(
                    "plan_step_conflict",
                    "Step '" + step.StepId + "' writes parameter '" + name + "' on view " +
                    viewId + ", but step '" + lock_.StepId + "' applies template '" +
                    lock_.TemplateName + "' which controls that parameter; once the template " +
                    "is applied Revit refuses the write with view_controlled_by_template. " +
                    "Detach the parameter from the template first — set_view_template_inclusion(" +
                    "templateId=" + lock_.TemplateId + ", parameters=[\"" + name +
                    "\"], included=false) — or write the value into the template itself " +
                    "(viewIds=[" + lock_.TemplateId + "]). Full per-step diff is in steps[].",
                    results);
            }
        }

        return null;
    }
}