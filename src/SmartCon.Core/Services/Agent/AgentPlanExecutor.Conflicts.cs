using System.Globalization;
using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    private readonly struct WriteTarget
    {
        public WriteTarget(long viewId, string section)
        {
            ViewId = viewId;
            Section = section;
        }

        public long ViewId { get; }

        public string Section { get; }
    }

    private readonly struct LockedSetting
    {
        public LockedSetting(long templateId, string templateName, string stepId)
        {
            TemplateId = templateId;
            TemplateName = templateName;
            StepId = stepId;
        }

        public long TemplateId { get; }

        public string TemplateName { get; }

        public string StepId { get; }
    }

    /// <summary>
    /// Cross-step preflight: a template applied by one step takes over crop,
    /// visibility and view range of its views, so a later step that writes one
    /// of those settings is guaranteed to fail with view_controlled_by_template.
    /// The dry-run delta of apply_view_template carries the controlled set;
    /// set_view_template_inclusion releases the lock for its section.
    /// </summary>
    private static AgentPlanExecutionResult? FindStepConflict(
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results)
    {
        var locks = new Dictionary<(long ViewId, string Section), LockedSetting>();

        foreach (var step in steps)
        {
            var result = results.FirstOrDefault(item => item.StepId == step.StepId);

            if (IsTool(step, "apply_view_template"))
            {
                if (result?.DryRunResult is { Success: true, Data: { } data }
                    && data.ValueKind == JsonValueKind.Object)
                {
                    AddTemplateLocks(data, step, locks);
                }

                continue;
            }

            if (IsTool(step, "set_view_template_inclusion"))
            {
                ReleaseTemplateLocks(step, result, locks);
                continue;
            }

            foreach (var target in ReadWriteTargets(step))
            {
                if (!locks.TryGetValue((target.ViewId, target.Section), out var locked))
                    continue;

                var remedy = target.Section == "crop"
                    ? "Crop cannot be detached from a view template in Revit: remove the " +
                      "template from view " + target.ViewId + " or drop the crop step."
                    : "Add set_view_template_inclusion(templateId=" + locked.TemplateId +
                      ", parameters=[\"" + target.Section + "\"], included=false) before step '" +
                      step.StepId + "', or remove the conflicting write.";

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

    private static void AddTemplateLocks(
        JsonElement data,
        AgentPlanStep step,
        IDictionary<(long, string), LockedSetting> locks)
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

            var templateName = GetString(item, "templateName") ?? "#" + templateId;
            if (!item.TryGetProperty("controls", out var controls)
                || controls.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            AddLock(locks, viewId, "crop", "crop", templateId, templateName, step.StepId);
            AddLock(locks, viewId, "visibility", "visibility", templateId, templateName, step.StepId);
            AddLock(locks, viewId, "viewRange", "viewRange", templateId, templateName, step.StepId);

            void AddLock(
                IDictionary<(long, string), LockedSetting> target,
                long id,
                string property,
                string section,
                long template,
                string name,
                string stepId)
            {
                if (controls.TryGetProperty(property, out var enabled)
                    && enabled.ValueKind == JsonValueKind.True)
                {
                    target[(id, section)] = new LockedSetting(template, name, stepId);
                }
            }
        }
    }

    private static void ReleaseTemplateLocks(
        AgentPlanStep step,
        AgentPlanStepResult? result,
        IDictionary<(long, string), LockedSetting> locks)
    {
        var released = new List<(long TemplateId, string Section)>();

        if (result?.DryRunResult is { Success: true, Data: { } data }
            && data.ValueKind == JsonValueKind.Object)
        {
            ReadInclusionChanges(data, released);
        }

        if (released.Count == 0)
        {
            try
            {
                using var document = JsonDocument.Parse(step.ArgumentsJson);
                var root = document.RootElement;
                if (!TryGetLong(root, "templateId", out var templateId))
                    return;

                var defaultIncluded = GetBool(root, "included", false);
                AddSections(root, templateId, defaultIncluded, released);
            }
            catch (JsonException)
            {
                return;
            }
        }

        foreach (var (templateId, section) in released)
        {
            foreach (var key in locks.Keys.ToList())
            {
                if (key.Item2 == section && locks[key].TemplateId == templateId)
                    locks.Remove(key);
            }
        }
    }

    private static void ReadInclusionChanges(
        JsonElement data,
        ICollection<(long TemplateId, string Section)> released)
    {
        if (!TryGetLong(data, "templateId", out var templateId))
            return;

        if (!data.TryGetProperty("parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in parameters.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (GetBool(item, "included", false))
                continue;

            var section = GetString(item, "section")
                          ?? SectionOf(GetString(item, "builtInName"));
            if (section is not null)
                released.Add((templateId, section));
        }
    }

    private static void AddSections(
        JsonElement args,
        long templateId,
        bool defaultIncluded,
        ICollection<(long TemplateId, string Section)> released)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return;

        if (args.TryGetProperty("parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in parameters.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || defaultIncluded)
                    continue;

                var section = SectionOf(item.GetString());
                if (section is not null)
                    released.Add((templateId, section));
            }
        }

        if (!args.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var included = GetBool(item, "included", defaultIncluded);
            if (included)
                continue;

            var section = SectionOf(GetString(item, "parameter"));
            if (section is not null)
                released.Add((templateId, section));
        }
    }

    private static IReadOnlyList<WriteTarget> ReadWriteTargets(AgentPlanStep step)
    {
        var targets = new List<WriteTarget>();
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(step.ArgumentsJson) ? "{}" : step.ArgumentsJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return targets;
        }

        var sections = ReadSections(root);
        var explicitSections = root.TryGetProperty("sections", out var sectionsNode)
                               && sectionsNode.ValueKind == JsonValueKind.Array;

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryGetLong(item, "viewId", out var viewId))
                {
                    continue;
                }

                foreach (var section in sections)
                {
                    if (explicitSections || HasSection(item, section))
                        targets.Add(new WriteTarget(viewId, section));
                }
            }

            return targets;
        }

        var viewIds = ReadViewIds(root);
        if (viewIds.Count == 0)
            return targets;

        foreach (var viewId in viewIds)
        {
            foreach (var section in sections)
            {
                if (explicitSections || HasSection(root, section))
                    targets.Add(new WriteTarget(viewId, section));
            }
        }

        return targets;
    }

    private static IReadOnlyList<string> ReadSections(JsonElement root)
    {
        var result = new List<string>();
        if (root.TryGetProperty("sections", out var sections)
            && sections.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sections.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                var value = item.GetString();
                if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
                {
                    Add("crop");
                    Add("visibility");
                    Add("viewRange");
                    continue;
                }

                var section = SectionOf(value);
                if (section is not null)
                    Add(section);
            }

            return result;

            void Add(string section)
            {
                if (!result.Contains(section))
                    result.Add(section);
            }
        }

        result.Add("crop");
        result.Add("visibility");
        result.Add("viewRange");
        return result;
    }

    private static bool HasSection(JsonElement element, string section)
    {
        if (!element.TryGetProperty(section, out var value))
            return false;

        return value.ValueKind is not JsonValueKind.Null and not JsonValueKind.False;
    }

    private static IReadOnlyList<long> ReadViewIds(JsonElement root)
    {
        var result = new List<long>();
        foreach (var property in new[] { "viewId", "viewIds", "targetViewIds" })
        {
            if (!root.TryGetProperty(property, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var single))
            {
                result.Add(single);
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var id))
                    result.Add(id);
            }
        }

        return result.Distinct().ToList();
    }

    private static string? SectionOf(string? parameterName)
    {
        var normalized = NormalizeKey(parameterName);
        return normalized switch
        {
            "CROP" or "VIEWERCROPREGION" => "crop",
            "VISIBILITY" or "VG" or "VISGRAPHICSMODEL" or "VISGRAPHICSANNOTATION"
                or "MODEL" or "ANNOTATION" => "visibility",
            "VIEWRANGE" or "PLANVIEWRANGE" => "viewRange",
            _ => null
        };
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value!;
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    private static bool TryGetLong(JsonElement element, string property, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var node))
            return false;

        return node.ValueKind switch
        {
            JsonValueKind.Number => node.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(
                node.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string property, bool fallback) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}