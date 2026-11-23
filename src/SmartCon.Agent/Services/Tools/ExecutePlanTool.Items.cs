using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed partial class ExecutePlanTool
{
    /// <summary>
    /// Step items projection for the report: drops changed[] cards that only
    /// duplicate what the step's created* blocks already describe (ORCH-7 —
    /// reporting the same creation twice doubled the context) and, for the
    /// preview, renames ids of objects the dry run would create with a preview
    /// prefix (C7-05/C4-11).
    /// </summary>
    private static JsonElement? TransformItems(AgentPlanStepResult step, bool apply)
    {
        var data = StepItems(step, apply);
        if (data is not { ValueKind: JsonValueKind.Object })
            return data;

        System.Text.Json.Nodes.JsonNode? node;
        try
        {
            node = System.Text.Json.Nodes.JsonNode.Parse(data.Value.GetRawText());
        }
        catch (JsonException)
        {
            return data;
        }

        if (node is not System.Text.Json.Nodes.JsonObject root)
            return data;

        var changed = false;
        DedupeCreatedInChanged(root, ref changed);

        System.Text.Json.Nodes.JsonObject? renamed = null;
        if (!apply)
        {
            var renameChanged = false;
            renamed = RenameNode(root, insideCreatedBlock: false, ref renameChanged)
                as System.Text.Json.Nodes.JsonObject;
            changed |= renameChanged;
        }

        if (!changed)
            return data;

        return JsonSerializer.SerializeToElement(renamed ?? root, SerializerOptions);
    }

    /// <summary>
    /// Write tools report the objects they created twice: once as changed[] diff
    /// cards and once in the created* block with the created ids. The report
    /// keeps the created* block (it carries the ids) and drops the duplicate
    /// cards, replacing them with a duplicatesInCreated counter so the numbers
    /// still add up (ORCH-7).
    /// </summary>
    private static void DedupeCreatedInChanged(
        System.Text.Json.Nodes.JsonObject root,
        ref bool changed)
    {
        if (root["changed"] is not System.Text.Json.Nodes.JsonArray changedArray
            || changedArray.Count == 0)
        {
            return;
        }

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasCreatedBlock = false;
        foreach (var pair in root)
        {
            if (!pair.Key.StartsWith("created", StringComparison.OrdinalIgnoreCase)
                || pair.Value is null)
            {
                continue;
            }

            hasCreatedBlock = true;
            switch (pair.Value)
            {
                case System.Text.Json.Nodes.JsonObject createdObject:
                    CollectIdentities(createdObject, identities);
                    break;
                case System.Text.Json.Nodes.JsonArray createdArray:
                    foreach (var item in createdArray)
                    {
                        if (item is System.Text.Json.Nodes.JsonObject createdItem)
                            CollectIdentities(createdItem, identities);
                    }

                    break;
            }
        }

        if (!hasCreatedBlock || identities.Count == 0)
            return;

        var kept = new System.Text.Json.Nodes.JsonArray();
        var duplicates = 0;
        foreach (var item in changedArray)
        {
            if (item is System.Text.Json.Nodes.JsonObject card
                && MatchesAnyIdentity(card, identities))
            {
                duplicates++;
                continue;
            }

            // The node still belongs to changedArray; re-parenting a live node
            // throws, so move a clone and let the original go with its array.
            kept.Add(item is null ? null : System.Text.Json.Nodes.JsonNode.Parse(item.ToJsonString()));
        }

        if (duplicates == 0)
            return;

        root["changed"] = kept;
        root["duplicatesInCreated"] = duplicates;
        changed = true;
    }

    private static void CollectIdentities(
        System.Text.Json.Nodes.JsonObject created,
        ISet<string> identities)
    {
        foreach (var pair in created)
        {
            switch (pair.Value)
            {
                case System.Text.Json.Nodes.JsonValue { } value
                    when IsNameKey(pair.Key) && value.TryGetValue<string>(out var name)
                    && name is not null && !string.IsNullOrWhiteSpace(name):
                    identities.Add(name);
                    break;
                case System.Text.Json.Nodes.JsonValue { } idValue
                    when IsIdKey(pair.Key) && idValue.TryGetValue<long>(out var id):
                    identities.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }
    }

    private static bool MatchesAnyIdentity(
        System.Text.Json.Nodes.JsonObject card,
        ISet<string> identities)
    {
        foreach (var pair in card)
        {
            if (!IsNameKey(pair.Key) && !IsIdKey(pair.Key))
                continue;

            var text = pair.Value switch
            {
                System.Text.Json.Nodes.JsonValue { } value when value.TryGetValue<string>(out var s) => s,
                System.Text.Json.Nodes.JsonValue { } value when value.TryGetValue<long>(out var l) =>
                    l.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null
            };

            // net48 BCL: no [NotNullWhen] on IsNullOrWhiteSpace — check null first.
            if (text is not null && !string.IsNullOrWhiteSpace(text) && identities.Contains(text))
                return true;
        }

        return false;
    }

    private static bool IsNameKey(string key) =>
        string.Equals(key, "name", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdKey(string key) =>
        key.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "id", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Nested tool failure details for a failed step: the tool's error code,
    /// message and the first failed[] reasons. Without them a step error says
    /// only "dry_run_failed" and the model has to re-run the tool to learn the
    /// cause (C4-08).
    /// </summary>
    private static object? StepError(AgentPlanStepResult step, bool apply)
    {
        var result = apply
            ? step.ApplyResult ?? step.DryRunResult
            : step.DryRunResult ?? step.ApplyResult;
        if (result is null || result.Success)
            return null;

        return new
        {
            code = result.ErrorCode,
            message = result.Message,
            failed = FirstFailedReasons(result, max: 3)
        };
    }

    private static IReadOnlyList<string> FirstFailedReasons(ToolResult result, int max)
    {
        var reasons = new List<string>();
        if (result.Data is not { ValueKind: JsonValueKind.Object } data
            || !data.TryGetProperty("failed", out var failed)
            || failed.ValueKind != JsonValueKind.Array)
        {
            return reasons;
        }

        foreach (var item in failed.EnumerateArray())
        {
            if (reasons.Count >= max)
                break;

            var reason = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("reason", out var reasonValue)
                    && reasonValue.ValueKind == JsonValueKind.String => reasonValue.GetString(),
                JsonValueKind.Object when item.TryGetProperty("message", out var messageValue)
                    && messageValue.ValueKind == JsonValueKind.String => messageValue.GetString(),
                _ => null
            };

            // net48 BCL has no [NotNullWhen] on IsNullOrWhiteSpace: the explicit
            // null check keeps the nullable analysis happy on both TFMs.
            if (reason is not null && !string.IsNullOrWhiteSpace(reason))
                reasons.Add(reason);
        }

        if (reasons.Count < max && failed.GetArrayLength() > max)
            reasons.Add("+" + (failed.GetArrayLength() - max) + " more");

        return reasons;
    }

    /// <summary>
    /// Builds renamed copies bottom-up (JsonNode cannot live in two parents at
    /// once, so mutating while enumerating is off-limits). Preview-only rename
    /// of provisional ids lives in <see cref="TransformItems"/> (C7-05/C4-11):
    /// every <c>*Id</c> key inside <c>created*</c> blocks (and every
    /// <c>wouldCreate*Id</c>/<c>new*Id</c> key) gets a <c>preview</c> prefix so
    /// the model does not read a rolled-back dry-run id as a real element id.
    /// </summary>
    private static System.Text.Json.Nodes.JsonNode? RenameNode(
        System.Text.Json.Nodes.JsonNode? node,
        bool insideCreatedBlock,
        ref bool changed)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                var renamed = new System.Text.Json.Nodes.JsonObject();
                foreach (var pair in obj)
                {
                    var key = pair.Key;
                    var childCreated = insideCreatedBlock
                                       || key.StartsWith("created", StringComparison.OrdinalIgnoreCase);
                    if (IsProvisionalIdKey(key, childCreated))
                    {
                        key = "preview" + char.ToUpperInvariant(key[0]) + key.Substring(1);
                        changed = true;
                    }

                    renamed[key] = RenameNode(pair.Value, childCreated, ref changed);
                }

                return renamed;

            case System.Text.Json.Nodes.JsonArray array:
                var renamedArray = new System.Text.Json.Nodes.JsonArray();
                foreach (var item in array)
                    renamedArray.Add(RenameNode(item, insideCreatedBlock, ref changed));

                return renamedArray;

            default:
                return CloneLeaf(node);
        }
    }

    private static System.Text.Json.Nodes.JsonNode? CloneLeaf(System.Text.Json.Nodes.JsonNode? node) =>
        node is null
            ? null
            : System.Text.Json.Nodes.JsonNode.Parse(node.ToJsonString());

    private static bool IsProvisionalIdKey(string key, bool insideCreatedBlock)
    {
        if (!key.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
            return false;

        if (key.StartsWith("preview", StringComparison.OrdinalIgnoreCase))
            return false;

        return insideCreatedBlock
               || key.StartsWith("wouldCreate", StringComparison.OrdinalIgnoreCase)
               || key.StartsWith("new", StringComparison.OrdinalIgnoreCase);
    }
}
