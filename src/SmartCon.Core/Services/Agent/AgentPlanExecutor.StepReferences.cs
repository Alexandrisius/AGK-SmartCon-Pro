using System.Text.Json;
using System.Text.Json.Nodes;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    /// <summary>
    /// Key of a step reference inside a plan step's arguments: {"$step":
    /// "&lt;stepId&gt;", "path": "&lt;dot path&gt;"}. It is replaced by the value the
    /// path addresses in the referenced step's apply result, so "create N and
    /// immediately configure it" (create_sheet → create_viewports,
    /// create_schedule → place_schedule_on_sheet) stays one plan instead of
    /// falling apart into calls that re-read the created id by name (S.42). The
    /// orchestrator's {"$result": …} references work between tool calls; this
    /// works between the steps of one plan.
    /// </summary>
    public const string StepReferenceKey = "$step";

    private const string StepReferencePathKey = "path";

    /// <summary>Cheapest check: the marker substring, then a real parse.</summary>
    public static bool ContainsStepReference(string? argumentsJson) =>
        !string.IsNullOrEmpty(argumentsJson)
        && argumentsJson!.IndexOf("\"" + StepReferenceKey + "\":", StringComparison.Ordinal) >= 0;

    /// <summary>Every stepId the {"$step": …} references of the arguments point at.</summary>
    public static IReadOnlyList<string> ReferencedStepIds(string? argumentsJson)
    {
        var ids = new List<string>();
        if (!ContainsStepReference(argumentsJson))
            return ids;

        try
        {
            if (JsonNode.Parse(argumentsJson!) is JsonNode root)
                Collect(root, ids);
        }
        catch (JsonException)
        {
            // Invalid arguments: the validator reports that; nothing to collect.
        }

        return ids;

        static void Collect(JsonNode node, List<string> ids)
        {
            if (node is JsonObject obj)
            {
                if (IsReference(obj, out var stepId))
                    ids.Add(stepId!);

                foreach (var pair in obj)
                {
                    if (pair.Value is not null)
                        Collect(pair.Value, ids);
                }

                return;
            }

            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is not null)
                        Collect(item, ids);
                }
            }
        }
    }

    private static bool IsReference(JsonObject obj, out string? stepId)
    {
        stepId = null;
        if (obj.Count != 2
            || obj[StepReferenceKey] is not JsonValue stepValue
            || !stepValue.TryGetValue<string>(out var step)
            || obj[StepReferencePathKey] is not JsonValue pathValue
            || !pathValue.TryGetValue<string>(out var path)
            || string.IsNullOrWhiteSpace(step)
            || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        stepId = step;
        return true;
    }

    /// <summary>
    /// Replaces every {"$step": …} node with the value it addresses in the
    /// referenced step's apply result. Returns the original text untouched when
    /// there is nothing to replace, and null with <paramref name="error"/> set
    /// when a reference cannot be resolved (unknown step, failed step, missing
    /// payload, unresolvable path). Segments are property names or array
    /// indexes ("created.levelId", "createdViews.0.viewId"); a dot inside a
    /// property name is not supported — bracket addressing is the read-back
    /// resolver's job, not this one.
    /// </summary>
    public static string? TryResolveStepReferences(
        string argumentsJson,
        IReadOnlyDictionary<string, AgentPlanStepResult> results,
        out string? error)
    {
        error = null;
        if (!ContainsStepReference(argumentsJson))
            return argumentsJson;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        }
        catch (JsonException ex)
        {
            error = "the arguments are not valid JSON: " + ex.Message;
            return null;
        }

        if (root is null)
            return argumentsJson;

        var replaced = false;
        var resolved = ResolveClone(root, results, ref replaced, out error);
        if (error is not null)
            return null;

        return replaced && resolved is not null ? resolved.ToJsonString() : argumentsJson;
    }

    private static JsonNode? ResolveClone(
        JsonNode? node,
        IReadOnlyDictionary<string, AgentPlanStepResult> results,
        ref bool replaced,
        out string? error)
    {
        error = null;
        if (node is null)
            return null;

        if (node is JsonObject reference && IsReference(reference, out var stepId))
        {
            var value = ResolveValue(
                stepId!,
                ((JsonValue)reference[StepReferencePathKey]!).GetValue<string>(),
                results,
                out error);
            if (error is not null)
                return null;

            replaced = true;
            return value;
        }

        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var pair in obj)
            {
                copy[pair.Key] = ResolveClone(pair.Value, results, ref replaced, out error);
                if (error is not null)
                    return null;
            }

            return copy;
        }

        if (node is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (var item in array)
            {
                copy.Add(ResolveClone(item, results, ref replaced, out error));
                if (error is not null)
                    return null;
            }

            return copy;
        }

        // JsonNode cannot live in two parents; a fresh parse is the cheap clone.
        return JsonNode.Parse(node.ToJsonString());
    }

    private static JsonNode? ResolveValue(
        string stepId,
        string path,
        IReadOnlyDictionary<string, AgentPlanStepResult> results,
        out string? error)
    {
        error = null;
        if (!results.TryGetValue(stepId, out var step) || step.ApplyResult is null)
        {
            error = "step '" + stepId + "' has no apply result (it failed, was skipped, or " +
                    "has not run — is it in dependsOn and before this step?)";
            return null;
        }

        if (!step.ApplyResult.Success)
        {
            error = "step '" + stepId + "' failed (" +
                    (step.ApplyResult.ErrorCode ?? "unknown") + "), so it produced no object";
            return null;
        }

        if (step.ApplyResult.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            error = "step '" + stepId + "' returned no data payload";
            return null;
        }

        JsonNode? cursor;
        try
        {
            cursor = JsonNode.Parse(data.GetRawText());
        }
        catch (JsonException)
        {
            error = "step '" + stepId + "' returned unreadable data";
            return null;
        }

        foreach (var segment in path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = cursor switch
            {
                JsonArray array when int.TryParse(segment, out var index)
                    => index >= 0 && index < array.Count ? array[index] : null,
                JsonObject obj => obj.TryGetPropertyValue(segment, out var child) ? child : null,
                _ => null
            };

            if (cursor is null)
            {
                error = "path '" + path + "' addresses nothing in the result of step '" +
                        stepId + "'";
                return null;
            }
        }

        // The cursor is still attached to the freshly parsed result tree; the
        // caller inserts the value into the arguments tree, and a JsonNode
        // cannot live in two parents.
        if (cursor is not JsonNode resolvedValue)
        {
            error = "path '" + path + "' addresses nothing in the result of step '" +
                    stepId + "'";
            return null;
        }

        return JsonNode.Parse(resolvedValue.ToJsonString());
    }
}
