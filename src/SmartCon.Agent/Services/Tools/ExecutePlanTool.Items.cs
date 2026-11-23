using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed partial class ExecutePlanTool
{
    /// <summary>
    /// Step items projection for the report: drops changed[] cards that only
    /// duplicate what the step's created* blocks already describe (ORCH-7 —
    /// reporting the same creation twice doubled the context) and, for the
    /// preview, renames the ids of the objects the dry run would create with a
    /// preview prefix (C7-05/C4-11).
    /// </summary>
    private static JsonElement? TransformItems(AgentPlanStepResult step, bool apply)
    {
        var data = StepItems(step, apply);
        if (data is not { ValueKind: JsonValueKind.Object })
            return data;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(data.Value.GetRawText());
        }
        catch (JsonException)
        {
            return data;
        }

        if (node is not JsonObject root)
            return data;

        var changed = false;
        DedupeCreatedInChanged(root, ref changed);

        if (!apply)
        {
            var renamed = RenameNode(root, createdBlockKey: null, ref changed);
            if (changed)
                return JsonSerializer.SerializeToElement(renamed, SerializerOptions);
        }

        return changed ? JsonSerializer.SerializeToElement(root, SerializerOptions) : data;
    }

    // ── created / changed dedupe ─────────────────────────────────────────────

    /// <summary>One object the step created, as a created* block describes it.</summary>
    private sealed class CreatedObject
    {
        public long? Id { get; init; }

        public string? Name { get; init; }
    }

    /// <summary>
    /// Write tools report the objects they created twice: once as changed[] diff
    /// cards and once in the created* block with the created ids. The report
    /// keeps the created* block (it carries the ids) and drops the duplicate
    /// cards, replacing them with a duplicatesInCreated counter so the numbers
    /// still add up (ORCH-7).
    /// </summary>
    private static void DedupeCreatedInChanged(JsonObject root, ref bool changed)
    {
        if (root["changed"] is not JsonArray changedArray || changedArray.Count == 0)
            return;

        var created = CollectCreated(root);
        if (created.Count == 0)
            return;

        var kept = new JsonArray();
        var duplicates = 0;
        foreach (var item in changedArray)
        {
            if (item is JsonObject card && IsDuplicateCard(card, created))
            {
                duplicates++;
                continue;
            }

            // The node still belongs to changedArray; re-parenting a live node
            // throws, so move a clone and let the original go with its array.
            kept.Add(item is null ? null : JsonNode.Parse(item.ToJsonString()));
        }

        if (duplicates == 0)
            return;

        root["changed"] = kept;
        root["duplicatesInCreated"] = duplicates;
        changed = true;
    }

    /// <summary>The objects listed by every created* block of the step result.</summary>
    private static List<CreatedObject> CollectCreated(JsonObject root)
    {
        var created = new List<CreatedObject>();
        foreach (var pair in root)
        {
            if (!pair.Key.StartsWith("created", StringComparison.OrdinalIgnoreCase) || pair.Value is null)
                continue;

            switch (pair.Value)
            {
                case JsonObject createdObject:
                    AddCreated(createdObject, pair.Key, created);
                    break;
                case JsonArray createdArray:
                    foreach (var item in createdArray)
                    {
                        if (item is JsonObject createdItem)
                            AddCreated(createdItem, pair.Key, created);
                    }

                    break;
            }
        }

        return created;
    }

    private static void AddCreated(JsonObject entry, string blockKey, List<CreatedObject> created)
    {
        TryGetCreatedIdKey(entry, blockKey, out var id);
        var name = ReadName(entry);
        if (id.HasValue || name is not null)
            created.Add(new CreatedObject { Id = id, Name = name });
    }

    /// <summary>
    /// A changed[] card is the same object as a created entry when both carry
    /// ids and they are equal, or — when one of them has no id, as in a dry run
    /// that reports viewId:null and cards without id — when the names are equal.
    /// Ids the card merely references (a view, a template) never take part, so a
    /// modification of another object survives the dedupe (review r1 #8).
    /// </summary>
    private static bool IsDuplicateCard(JsonObject card, List<CreatedObject> created)
    {
        var cardId = card["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id)
            ? id
            : (long?)null;
        var cardName = ReadName(card);
        foreach (var entry in created)
        {
            if (entry.Id.HasValue && cardId.HasValue)
            {
                if (entry.Id.Value == cardId.Value)
                    return true;

                continue;
            }

            if (entry.Name is not null
                && cardName is not null
                && string.Equals(entry.Name, cardName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Id keys our creating tools use for the object a <c>created</c> entry
    /// describes, in a fixed priority order. A bare <c>created</c> block has no
    /// entity in its name, and picking "the first *Id of the entry" made the
    /// choice depend on the serialization order of the tool's arguments
    /// (review r2, N6). Keys that reference other objects (levelId, templateId,
    /// categoryId, genLevelId…) are never in this list.
    /// </summary>
    private static readonly string[] OwnIdKeysInPriorityOrder =
        { "viewId", "scheduleId", "filterId", "sheetId", "familyId", "typeId", "materialId" };

    /// <summary>
    /// The key under which a created entry carries the id of the object it
    /// created: <c>&lt;entity&gt;Id</c> for a block that names its entity
    /// (createdViews → viewId), else <c>id</c>, else the first of
    /// <see cref="OwnIdKeysInPriorityOrder"/> the entry carries, else — when
    /// the entry has exactly one <c>*Id</c> key — that key; with several
    /// unknown <c>*Id</c> keys no id is picked and the entry is matched by name
    /// alone. Only this key describes the created object: levelId, templateId
    /// and the like point at objects that already existed and survive the
    /// apply (review r1 #3).
    /// </summary>
    private static string? TryGetCreatedIdKey(JsonObject entry, string blockKey, out long? id)
    {
        id = null;
        var noun = blockKey.Substring("created".Length);
        if (noun.Length > 1 && noun.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            noun = noun.Substring(0, noun.Length - 1);

        foreach (var pair in entry)
        {
            if (string.Equals(pair.Key, noun + "Id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(pair.Key, "id", StringComparison.OrdinalIgnoreCase))
            {
                id = ReadId(pair.Value);
                return pair.Key;
            }
        }

        foreach (var candidate in OwnIdKeysInPriorityOrder)
        {
            foreach (var pair in entry)
            {
                if (string.Equals(pair.Key, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    id = ReadId(pair.Value);
                    return pair.Key;
                }
            }
        }

        string? singleKey = null;
        long? singleId = null;
        foreach (var pair in entry)
        {
            if (!pair.Key.EndsWith("Id", StringComparison.Ordinal))
                continue;

            if (singleKey is not null)
                return null;

            singleKey = pair.Key;
            singleId = ReadId(pair.Value);
        }

        id = singleId;
        return singleKey;
    }

    private static long? ReadId(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var id) ? id : null;

    private static string? ReadName(JsonObject node) =>
        node["name"] is JsonValue value && value.TryGetValue<string>(out var name) && name is not null
            ? name
            : null;

    // ── preview ids ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds renamed copies bottom-up (JsonNode cannot live in two parents at
    /// once, so mutating while enumerating is off-limits). Preview-only (the
    /// apply report keeps the real ids). Two kinds of keys are provisional:
    /// the created object's own id inside a created* block and the keys a tool
    /// announces itself (wouldCreate*Id, new*Id) — see
    /// <see cref="TryGetCreatedIdKey"/> and <see cref="IsAnnouncedProvisionalKey"/>.
    /// </summary>
    private static JsonNode? RenameNode(JsonNode? node, string? createdBlockKey, ref bool changed)
    {
        switch (node)
        {
            case JsonObject entry when createdBlockKey is not null:
                // One created entry: only its own id is the id of an object that
                // does not exist yet.
                var renamedEntry = new JsonObject();
                var idKey = TryGetCreatedIdKey(entry, createdBlockKey, out _);
                foreach (var pair in entry)
                {
                    var key = pair.Key;
                    if (idKey is not null && IsProvisionalKey(key, idKey))
                    {
                        key = PreviewKey(key);
                        changed = true;
                    }

                    renamedEntry[key] = RenameNode(pair.Value, createdBlockKey: null, ref changed);
                }

                return renamedEntry;

            case JsonObject plain:
                var renamed = new JsonObject();
                foreach (var pair in plain)
                {
                    var key = pair.Key;
                    var childBlock = pair.Value is JsonObject or JsonArray
                                     && pair.Key.StartsWith("created", StringComparison.OrdinalIgnoreCase)
                        ? pair.Key
                        : null;
                    if (childBlock is null && IsAnnouncedProvisionalKey(key))
                    {
                        key = PreviewKey(key);
                        changed = true;
                    }

                    renamed[key] = RenameNode(pair.Value, childBlock, ref changed);
                }

                return renamed;

            case JsonArray array:
                var renamedArray = new JsonArray();
                foreach (var item in array)
                    renamedArray.Add(RenameNode(item, createdBlockKey, ref changed));

                return renamedArray;

            default:
                return node is null ? null : JsonNode.Parse(node.ToJsonString());
        }
    }

    private static bool IsProvisionalKey(string key, string idKey) =>
        string.Equals(key, idKey, StringComparison.Ordinal)
        && !key.StartsWith("preview", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A key a tool uses to announce an id that does not exist yet:
    /// wouldCreate*Id (create_view_filter) or new*Id.
    /// </summary>
    private static bool IsAnnouncedProvisionalKey(string key) =>
        key.EndsWith("Id", StringComparison.Ordinal)
        && !key.StartsWith("preview", StringComparison.OrdinalIgnoreCase)
        && (key.StartsWith("wouldCreate", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("new", StringComparison.OrdinalIgnoreCase));

    private static string PreviewKey(string key) =>
        "preview" + char.ToUpperInvariant(key[0]) + key.Substring(1);

    // ── step error ───────────────────────────────────────────────────────────

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

        var examined = 0;
        foreach (var item in failed.EnumerateArray())
        {
            if (reasons.Count >= max)
                break;

            examined++;
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

        // Every row beyond the examined ones is reported as cut — also when the
        // kept reasons already fill the limit (review r1 #7).
        var cut = failed.GetArrayLength() - examined;
        if (cut > 0)
            reasons.Add("+" + cut.ToString(CultureInfo.InvariantCulture) + " more");

        return reasons;
    }
}
