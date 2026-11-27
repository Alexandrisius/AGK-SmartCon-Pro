using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Structural size control for tool results (mvp-plan §11, Phase 2 context budget).
/// When a result exceeds the character budget, arrays are shortened from the end and
/// the payload gets <c>&lt;name&gt;Returned</c>, <c>&lt;name&gt;Total</c>,
/// <c>truncated=true</c> and a <c>hint</c>. The JSON stays valid at every step —
/// the old "cut the string in half" approach produced unparseable fragments.
/// </summary>
public static class ToolResultBudget
{
    public const int DefaultMaxCharacters = 40000;

    /// <summary>
    /// Budget of the pack loader (<c>load_tool_pack</c>). It returns one markdown
    /// skill per requested pack, and a skill is sent once per conversation — it
    /// cannot be requested again, so trimming it away loses the manual for the
    /// rest of the chat (#400).
    /// </summary>
    public const int MetaMaxCharacters = 90000;

    /// <summary>
    /// The family-parameter matrix tool carries a WHOLE migration inventory per
    /// call (41 families x 15+ shared parameters ≈ 90k compact chars): the
    /// default 40k trimmed it to 5 parameters per family and forced the model
    /// into a dozen single-family follow-up calls (live v18, owner report) —
    /// one 120k result is five times cheaper in context than the split.
    /// </summary>
    public const int FamilyMatrixMaxCharacters = 120000;

    /// <summary>Character budget for a tool result of the given tool.</summary>
    public static int MaxCharactersFor(string? toolName) =>
        string.Equals(toolName, "load_tool_pack", StringComparison.Ordinal)
            ? MetaMaxCharacters
            : string.Equals(toolName, "get_family_parameters", StringComparison.Ordinal)
                ? FamilyMatrixMaxCharacters
                : DefaultMaxCharacters;

    public const string TruncationHint =
        "The result was trimmed to fit the context budget. Narrow the request " +
        "(filters, smaller limit) or continue with offset/nextOffset instead of " +
        "repeating the same call.";

    public const string BlobHint =
        "The full result is stored under resultHandle: call read_tool_result with that " +
        "handle (and offset/limit or a path) instead of re-running the same call. To pass " +
        "a part of it to another tool, do not read it: put the reference object " +
        "{\"$result\":\"<resultHandle>\",\"path\":\"<path>\"} in place of that argument.";

    /// <summary>
    /// A digest keeps the small objects that say what the result contains
    /// (a summary, a selection, a note with a ready reference). Without them a
    /// trimmed result reads as "nothing found"; a large object would defeat the
    /// digest and stays out.
    /// </summary>
    private const int DigestInlineMaxCharacters = 4000;

    private static readonly string[] DigestInlineProperties =
    {
        "summary", "selection", "setParametersNote", "note",
        // S.45: the composition of a paged set must survive the digest, or the
        // model pages one row at a time to learn what the payload holds.
        "byCategory", "uniqueTypes", "sampleTypeNames"
    };

    private static readonly string[] TrimmableArrays =
    {
        "elements", "views", "levels", "groups", "parameters", "cards",
        "changed", "skipped", "failed", "items", "templates",
        "families", "types", "matchedTypes",
        "filters", "schedules", "sheets", "appliedViews",
        // execute_plan: the per-step diff is what the user approves; the digest
        // envelope must not swallow it without leaving planDigest to apply.
        "steps", "mismatches", "verified", "notPreviewed",
        // Tool signatures are recoverable (find_tools / a later load_tool_pack);
        // they yield before the pack skills, which are sent once per conversation
        // and cannot be requested again (#400).
        "schemas",
        // classify_types: the proposals for the report and the families left
        // uncertain; the ready set_parameters argument is taken by reference.
        "assignments", "uncertain", "alreadySet"
    };

    /// <summary>
    /// Properties kept verbatim when the payload is replaced by a digest. Pack
    /// skills belong here: load_tool_pack marks a skill as sent before the
    /// budget runs, so dropping the text would lose the manual for the whole
    /// conversation.
    /// </summary>
    private static readonly string[] DigestProtectedProperties =
    {
        "skills", "skillAlreadySent", "loaded", "appliedTo",
        // Without the digest the model cannot apply the plan it just previewed;
        // previewIncomplete is the honesty flag the apply decision relies on.
        "planDigest", "planDigestAlgorithm", "previewIncomplete", "phase", "goal"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Full JSON without any trimming (stored as a tool-result blob).</summary>
    public static string SerializeFull(ToolResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        return JsonSerializer.Serialize(result, SerializerOptions);
    }

    public static string Serialize(
        ToolResult result,
        int maxCharacters = DefaultMaxCharacters,
        string? resultHandle = null)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        var json = SerializeFull(result);
        if (json.Length <= maxCharacters)
            return json;

        return Trim(json, maxCharacters, resultHandle);
    }

    public static string Trim(
        string json,
        int maxCharacters = DefaultMaxCharacters,
        string? resultHandle = null)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (node is not JsonObject root)
            return json;

        var data = root["data"] as JsonObject;
        if (data is null)
            return json;

        var arrays = new List<(string Name, JsonArray Array, int OriginalCount)>();
        foreach (var name in TrimmableArrays)
        {
            if (data[name] is JsonArray array && array.Count > 0)
                arrays.Add((name, array, array.Count));
        }

        if (arrays.Count == 0)
            return AddEnvelope(root, maxCharacters, resultHandle);

        // The first trimmable array is the tool's primary page list (families,
        // elements, views...); its "returned" field must follow the trim, or the
        // answer says returned=173 next to familiesReturned=43.
        var primaryArrayName = arrays[0].Name;

        while (true)
        {
            var current = root.ToJsonString(SerializerOptions);
            if (current.Length <= maxCharacters)
                return current;

            var largest = arrays
                .Where(entry => entry.Array.Count > 0)
                .OrderByDescending(entry => entry.Array.Count)
                .FirstOrDefault();

            if (largest.Array is null)
                return AddEnvelope(root, maxCharacters, resultHandle);

            // Never shrink an array to zero: a payload with families=[] and
            // familiesTotal=5 reads as "nothing found" (stress-test #3). At one
            // element the digest envelope takes over instead.
            if (largest.Array.Count <= 1)
                return AddEnvelope(root, maxCharacters, resultHandle);

            var keep = largest.Array.Count / 2;
            while (largest.Array.Count > keep)
                largest.Array.RemoveAt(largest.Array.Count - 1);

            data[largest.Name + "Returned"] = largest.Array.Count;
            data[largest.Name + "Total"] = largest.OriginalCount;
            if (string.Equals(largest.Name, primaryArrayName, StringComparison.Ordinal)
                && data["returned"] is JsonValue returnedValue
                && returnedValue.TryGetValue<int>(out _))
            {
                data["returned"] = largest.Array.Count;
            }

            data["truncated"] = true;
            data["hint"] = resultHandle is null ? TruncationHint : BlobHint;
            if (resultHandle is not null)
                data["resultHandle"] = resultHandle;
            AppendTruncationNote(
                root, largest.Name, largest.Array.Count, largest.OriginalCount, resultHandle);
        }
    }

    /// <summary>
    /// Adds resultHandle into the data object of an UNTRIMMED serialized result
    /// (AlwaysStoreResult tools): the model sees the reference target in every
    /// call of the tool, not only in trimming envelopes (S.47, #532).
    /// </summary>
    public static string InjectResultHandle(string json, string resultHandle)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["data"] is not JsonObject data)
                return json;

            data["resultHandle"] = resultHandle;
            return root.ToJsonString(SerializerOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>
    /// Keeps the top-level message truthful after trimming: a message like
    /// "Returned details for 10 element(s)" must not be sent together with a
    /// one-element payload.
    /// </summary>
    private static void AppendTruncationNote(
        JsonObject root,
        string arrayName,
        int returned,
        int total,
        string? resultHandle)
    {
        if (root["message"] is not JsonValue messageValue
            || !messageValue.TryGetValue<string>(out var message)
            || message!.IndexOf("[truncated:", StringComparison.Ordinal) >= 0)
        {
            return;
        }

        root["message"] = message + " [truncated: " + returned + " of " + total + " " +
                          arrayName + " returned" +
                          (resultHandle is null
                              ? "; narrow the request"
                              : "; full result under resultHandle for read_tool_result") + "]";
    }

    private static string AddEnvelope(
        JsonObject root,
        int maxCharacters,
        string? resultHandle = null)
    {
        if (root["data"] is not JsonObject data)
            return root.ToJsonString(SerializerOptions);

        // No trimmable arrays left: replace the payload with a digest, keeping the
        // fields the model needs to react correctly (still valid JSON).
        var digest = new JsonObject
        {
            ["truncated"] = true,
            ["hint"] = resultHandle is null ? TruncationHint : BlobHint
        };

        foreach (var name in new[] { "total", "returned", "offset", "nextOffset", "dryRun" })
        {
            if (data[name] is { } value)
                digest[name] = value.DeepClone();
        }

        // Keep every "<name>Total" counter the tools report, so the model still
        // sees that matches exist after the payload was replaced by a digest.
        foreach (var property in data)
        {
            if (property.Key.EndsWith("Total", StringComparison.Ordinal)
                && property.Value is { } totalValue)
            {
                digest[property.Key] = totalValue.DeepClone();
            }
        }

        foreach (var name in DigestProtectedProperties)
        {
            if (data[name] is { } protectedValue)
                digest[name] = protectedValue.DeepClone();
        }

        if (data["affected"] is { } affected)
            digest["affected"] = affected.DeepClone();

        foreach (var name in DigestInlineProperties)
        {
            if (data[name] is { } small && small.ToJsonString(SerializerOptions).Length <= DigestInlineMaxCharacters)
                digest[name] = small.DeepClone();
        }

        if (resultHandle is not null)
            digest["resultHandle"] = resultHandle;

        // The digest hides the payload, so it must say what the payload holds:
        // the shape of every top-level key and the ready paths of the arrays
        // let the model pick a path for read_tool_result / a {"$result","path"}
        // reference without paging the stored result in first (DB-09).
        AppendStructure(digest, data);

        root["data"] = digest;
        if (root["message"] is JsonValue messageValue
            && messageValue.TryGetValue<string>(out var message)
            && message!.IndexOf("[truncated:", StringComparison.Ordinal) < 0)
        {
            root["message"] = message + " [truncated: payload replaced by a digest" +
                              (resultHandle is null
                                  ? "; narrow the request"
                                  : "; full result under resultHandle") + "]";
        }

        var json = root.ToJsonString(SerializerOptions);
        if (json.Length <= maxCharacters)
            return json;

        // Extremely long message/error text: keep only the essentials.
        var minimal = new JsonObject
        {
            ["success"] = root["success"]?.DeepClone() ?? false,
            ["errorCode"] = root["errorCode"]?.DeepClone(),
            ["message"] = root["message"]?.DeepClone(),
            ["affectedCount"] = root["affectedCount"]?.DeepClone() ?? 0,
            ["data"] = new JsonObject
            {
                ["truncated"] = true,
                ["hint"] = resultHandle is null ? TruncationHint : BlobHint
            }
        };

        if (resultHandle is not null)
            ((JsonObject)minimal["data"]!)["resultHandle"] = resultHandle;

        if (root["_repairs"] is { } repairs)
            minimal["_repairs"] = repairs.DeepClone();

        if (root["tool"] is { } tool)
            minimal["tool"] = tool.DeepClone();

        return minimal.ToJsonString(SerializerOptions);
    }

    private const int StructureMaxKeys = 40;
    private const int StructureMaxArrayPaths = 12;
    private const int StructureMaxDepth = 3;
    private const int StructureScalarMaxCharacters = 40;

    /// <summary>
    /// Adds <c>structure</c> (kind and size of every top-level key of the
    /// payload) and <c>arrayPaths</c> (the first arrays a dot path can address,
    /// with their length). Bounded: the digest must stay a digest.
    /// </summary>
    private static void AppendStructure(JsonObject digest, JsonObject data)
    {
        var structure = new JsonObject();
        var paths = new JsonArray();
        var pathCount = 0;
        foreach (var property in data)
        {
            if (structure.Count >= StructureMaxKeys)
                break;

            var description = Describe(property.Value);
            int? originalLength = null;
            if (property.Value is JsonArray && data[property.Key + "Total"] is JsonValue original
                && original.TryGetValue<int>(out var total))
            {
                // The array was halved on the way to the digest: the stored
                // result still has every element, and that is what a path leads to.
                originalLength = total;
                description["length"] = total;
            }

            structure[property.Key] = description;
            var before = pathCount;
            if (IsAddressable(property.Key))
                pathCount = CollectArrayPaths(property.Value, "data." + property.Key, 1, paths, pathCount);

            if (originalLength.HasValue && pathCount > before && paths[before] is JsonObject own)
                own["length"] = originalLength.Value;
        }

        digest["structure"] = structure;
        if (paths.Count > 0)
            digest["arrayPaths"] = paths;
    }

    private static JsonObject Describe(JsonNode? node)
    {
        var description = new JsonObject();
        switch (node)
        {
            case JsonObject objet:
                description["kind"] = "object";
                description["count"] = objet.Count;
                break;
            case JsonArray array:
                description["kind"] = "array";
                description["length"] = array.Count;
                break;
            case null:
                description["kind"] = "null";
                break;
            default:
                description["kind"] = "scalar";
                if (node!.ToJsonString(SerializerOptions).Length <= StructureScalarMaxCharacters)
                    description["value"] = node.DeepClone();
                break;
        }

        return description;
    }

    /// <summary>
    /// Depth-first search for non-empty arrays. Only the first element of an
    /// array is entered: the path shows the shape, not every item.
    /// </summary>
    private static int CollectArrayPaths(
        JsonNode? node,
        string path,
        int depth,
        JsonArray target,
        int count)
    {
        if (count >= StructureMaxArrayPaths || node is null)
            return count;

        if (node is JsonArray array)
        {
            if (array.Count == 0)
                return count;

            target.Add(new JsonObject { ["path"] = path, ["length"] = array.Count });
            count++;
            return depth >= StructureMaxDepth
                ? count
                : CollectArrayPaths(array[0], path + ".0", depth + 1, target, count);
        }

        if (node is not JsonObject objet || depth >= StructureMaxDepth)
            return count;

        foreach (var property in objet)
        {
            if (count >= StructureMaxArrayPaths)
                break;

            if (IsAddressable(property.Key))
                count = CollectArrayPaths(
                    property.Value, path + "." + property.Key, depth + 1, target, count);
        }

        return count;
    }

    /// <summary>AgentResultPath splits on '.' and '/': such keys have no path.</summary>
    private static bool IsAddressable(string key) =>
        key.IndexOf('.') < 0 && key.IndexOf('/') < 0;
}
