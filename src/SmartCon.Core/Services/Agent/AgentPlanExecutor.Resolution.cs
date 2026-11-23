using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
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

    /// <summary>
    /// True when the step depends (directly or transitively) on a write step.
    /// Such a step cannot see the dependency's change during a rolled-back dry run.
    /// </summary>
    private bool DependsOnWrite(
        AgentPlanStep step,
        IReadOnlyDictionary<string, AgentPlanStep> byId)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        return DependsOnWriteCore(step, byId, visiting);
    }

    /// <summary>
    /// The first write step reachable from <paramref name="step"/>'s dependsOn
    /// chain (direct or through read steps), so the preview_skip reason can name
    /// the exact blocking step instead of a generic flag.
    /// </summary>
    private AgentPlanStep? FirstWriteDependency(
        AgentPlanStep step,
        IReadOnlyDictionary<string, AgentPlanStep> byId)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        return FirstWriteDependencyCore(step, byId, visiting);
    }

    private AgentPlanStep? FirstWriteDependencyCore(
        AgentPlanStep step,
        IReadOnlyDictionary<string, AgentPlanStep> byId,
        ISet<string> visiting)
    {
        foreach (var dependency in step.DependsOn)
        {
            if (!byId.TryGetValue(dependency, out var dependentStep))
                continue;

            if (!visiting.Add(dependency))
                continue;

            if (IsWrite(dependentStep.ToolName))
                return dependentStep;

            var nested = FirstWriteDependencyCore(dependentStep, byId, visiting);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private bool DependsOnWriteCore(
        AgentPlanStep step,
        IReadOnlyDictionary<string, AgentPlanStep> byId,
        ISet<string> visiting)
    {
        foreach (var dependency in step.DependsOn)
        {
            if (!byId.TryGetValue(dependency, out var dependentStep))
                continue;

            if (!visiting.Add(dependency))
                continue;

            if (IsWrite(dependentStep.ToolName)
                || DependsOnWriteCore(dependentStep, byId, visiting))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Re-targets later plan steps to the family element Revit created during a
    /// previous load-back. Only the familyId key is rewritten: element ids of
    /// views, types and sheets are stable within the plan.
    /// </summary>
    private static string ApplyFamilyAliases(
        string argumentsJson,
        IDictionary<long, long> aliases)
    {
        if (aliases.Count == 0 || string.IsNullOrWhiteSpace(argumentsJson))
            return argumentsJson;

        try
        {
            if (JsonNode.Parse(argumentsJson) is not JsonObject root)
                return argumentsJson;

            if (!RewriteFamilyIds(root, aliases))
                return argumentsJson;

            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
    }

    private static bool RewriteFamilyIds(
        JsonNode node,
        IDictionary<long, long> aliases)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (string.Equals(pair.Key, "familyId", StringComparison.OrdinalIgnoreCase)
                    && pair.Value is JsonValue value
                    && value.TryGetValue<long>(out var id)
                    && aliases.TryGetValue(id, out var replacement))
                {
                    obj[pair.Key] = JsonValue.Create(replacement);
                    changed = true;
                    continue;
                }

                if (pair.Value is not null && RewriteFamilyIds(pair.Value, aliases))
                    changed = true;
            }

            return changed;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null && RewriteFamilyIds(item, aliases))
                    changed = true;
            }
        }

        return changed;
    }

    private static void CollectFamilyAliases(
        ToolResult result,
        IDictionary<long, long> aliases)
    {
        if (result.Data is not { ValueKind: JsonValueKind.Object } data)
            return;

        if (!TryReadLong(data, "previousFamilyId", out var previous)
            || !TryReadLong(data, "familyId", out var current)
            || previous == current)
        {
            return;
        }

        // Compress the chain: a → b, then b → c must leave a → c, otherwise a
        // third step that still carries the original id would resolve to the
        // stale intermediate element.
        foreach (var key in aliases.Keys.ToList())
        {
            if (aliases[key] == previous)
                aliases[key] = current;
        }

        aliases[previous] = current;
    }

    private static bool TryReadLong(JsonElement element, string property, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var node))
            return false;

        return node.ValueKind == JsonValueKind.Number
            ? node.TryGetInt64(out value)
            : node.ValueKind == JsonValueKind.String
              && long.TryParse(
                  node.GetString(),
                  NumberStyles.Integer,
                  CultureInfo.InvariantCulture,
                  out value);
    }
}