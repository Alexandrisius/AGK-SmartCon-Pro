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

    private static string? ResolvePath(JsonElement element, string path)
    {
        var current = element;
        foreach (var segment in path.Split('.'))
        {
            if (TryResolveById(current, segment, out var byId))
            {
                current = byId;
                continue;
            }

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

    /// <summary>
    /// Addresses a specific element inside an array: <c>byId[1704789].name</c>.
    /// Read-back results are arrays of cards, so index-based access easily checks
    /// the wrong object (live dogfood P0-2); byId matches the "id", "viewId",
    /// "elementId" or "levelId" property of an object anywhere in the current node.
    /// </summary>
    private static bool TryResolveById(JsonElement current, string segment, out JsonElement found)
    {
        found = default;
        if (!segment.StartsWith("byId[", StringComparison.Ordinal)
            || !segment.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        var raw = segment.Substring(5, segment.Length - 6).Trim().Trim('"', '\'');
        if (raw.Length == 0)
            return false;

        return TryFindById(current, raw, out found);
    }

    private static bool TryFindById(JsonElement element, string id, out JsonElement found)
    {
        found = default;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsIdProperty(property.Name)
                        && PropertyMatches(property.Value, id))
                    {
                        found = element;
                        return true;
                    }

                    if (TryFindById(property.Value, id, out found))
                        return true;
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindById(item, id, out found))
                        return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool IsIdProperty(string name) =>
        string.Equals(name, "id", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "viewId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "elementId", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "levelId", StringComparison.OrdinalIgnoreCase);

    private static bool PropertyMatches(JsonElement value, string id)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => string.Equals(
                value.GetRawText(), id, StringComparison.Ordinal),
            JsonValueKind.String => string.Equals(
                value.GetString(), id, StringComparison.Ordinal),
            _ => false
        };
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