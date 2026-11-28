using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartCon.Agent.Services.Tools;

/// <summary>
/// Resolves a dot/slash path inside the JSON of a stored tool result. The
/// reader (<c>read_tool_result</c>) and the argument reference of the
/// orchestrator address a stored result the same way.
/// </summary>
internal static class AgentResultPath
{
    // Non-ASCII text (Cyrillic system names, view names) must reach the model as
    // plain text: the default escaper turns every letter into \uXXXX, six
    // characters per letter, and the model misread the names.
    private static readonly JsonSerializerOptions TextOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// The node at <paramref name="path"/>, or null with <paramref name="error"/>
    /// set when the result is not JSON or the path does not exist.
    /// </summary>
    internal static JsonNode? Resolve(string json, string path, out string? error)
    {
        error = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            error = "The stored result is not valid JSON: " + ex.Message;
            return null;
        }

        foreach (var segment in SplitPath(path))
        {
            if (node is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(segment, out node))
                {
                    error = "Path segment '" + segment + "' was not found.";
                    return null;
                }
            }
            else if (node is JsonArray array)
            {
                if (!TryParseIndex(segment, array.Count, out var index))
                {
                    // Column projection: a property name applied to an array maps
                    // over the elements - data.rows.typeId yields the typeId of
                    // every row - so a $result argument produces a ready array of
                    // scalars instead of being limited to one cell (S.47, #532).
                    // The clone matters: the cells still sit inside the parsed
                    // document and cannot join another tree.
                    var mapped = new JsonArray();
                    foreach (var item in array)
                    {
                        if (item is JsonObject row && row.TryGetPropertyValue(segment, out var cell))
                            mapped.Add(Clone(cell));
                        else
                            mapped.Add(null);
                    }

                    node = mapped;
                    continue;
                }

                node = array[index];
            }
            else
            {
                error = "Path segment '" + segment + "' cannot be resolved: the value is a scalar.";
                return null;
            }
        }

        return node;
    }

    /// <summary>
    /// Projects several paths out of a stored result. Unlike <see cref="Resolve"/>,
    /// a property segment applied to an array maps over the elements, so
    /// <c>parameters.name</c> on an array of parameter rows yields an array of
    /// names. This is the read-side answer to "do not page 200k characters into
    /// the context just to aggregate three fields".
    /// </summary>
    internal static JsonNode? Project(
        string json,
        string? basePath,
        IReadOnlyList<string> paths,
        out string? error)
    {
        error = null;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            error = "The stored result is not valid JSON: " + ex.Message;
            return null;
        }

        var baseNode = root;
        if (!string.IsNullOrWhiteSpace(basePath))
        {
            baseNode = Resolve(json, basePath!, out error);
            if (error is not null)
                return null;
        }

        var result = new JsonObject();
        foreach (var path in paths)
        {
            var segments = SplitPath(path);
            if (segments.Length == 0)
            {
                error = "Projection path is empty.";
                return null;
            }

            if (!TryResolveProjected(baseNode, segments, 0, out var value))
            {
                error = "Path '" + path + "' was not found in the stored result.";
                return null;
            }

            result[path] = Clone(value);
        }

        return result;
    }

    private static bool TryResolveProjected(
        JsonNode? node,
        IReadOnlyList<string> segments,
        int index,
        out JsonNode? value)
    {
        value = null;
        if (index >= segments.Count)
        {
            value = node;
            return true;
        }

        var segment = segments[index];
        if (node is JsonObject obj)
        {
            if (!obj.TryGetPropertyValue(segment, out var child))
                return false;

            return TryResolveProjected(child, segments, index + 1, out value);
        }

        if (node is JsonArray array)
        {
            if (int.TryParse(
                    segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                if (!TryParseIndex(segment, array.Count, out var itemIndex))
                    return false;

                return TryResolveProjected(
                    array[itemIndex], segments, index + 1, out value);
            }

            var mapped = new JsonArray();
            var wildcard = segment == "*";
            foreach (var item in array)
            {
                if (!TryResolveProjected(
                        item, segments, wildcard ? index + 1 : index, out var mappedValue))
                {
                    // Keep positional alignment: parameters.name over a heterogeneous
                    // array yields a null placeholder instead of silently shortening.
                    mapped.Add(null);
                    continue;
                }

                mapped.Add(Clone(mappedValue));
            }

            value = mapped;
            return true;
        }

        return false;
    }

    private static bool TryParseIndex(string segment, int count, out int index)
    {
        index = 0;
        if (!int.TryParse(
                segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed < 0)
            parsed += count;

        if (parsed < 0 || parsed >= count)
            return false;

        index = parsed;
        return true;
    }

    private static string[] SplitPath(string path)
    {
        // Only [n] and [*] are index syntax: a JSON key that itself contains
        // brackets (not produced by current tools, but legal) stays addressable.
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            if (current.Length == 0)
                return;

            segments.Add(current.ToString());
            current.Clear();
        }

        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (character is '.' or '/')
            {
                Flush();
                continue;
            }

            if (character == '[')
            {
                var close = path.IndexOf(']', index + 1);
                if (close > index + 1)
                {
                    var inner = path.Substring(index + 1, close - index - 1);
                    if (inner == "*"
                        || int.TryParse(
                            inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        Flush();
                        segments.Add(inner);
                        index = close;
                        continue;
                    }
                }
            }

            current.Append(character);
        }

        Flush();
        return segments.ToArray();
    }

    private static JsonNode? Clone(JsonNode? node) =>
        node is null ? null : JsonNode.Parse(node.ToJsonString(TextOptions));

    /// <summary>
    /// The keys the caller can actually address, for error messages: the live
    /// session asked for "families.name" with no path and got a bare "not
    /// found" — the stored result is envelope-wrapped and the working root was
    /// "data.families", which the error never mentioned.
    /// </summary>
    internal static string RootsHint(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return string.Empty;

            var hint = "Top-level keys: " + string.Join(", ", KeysOf(root)) + ".";
            if (root.TryGetPropertyValue("data", out var data) && data is JsonObject dataObject)
            {
                hint += " Inside 'data': " + string.Join(", ", KeysOf(dataObject)) + ".";
            }

            return hint;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> KeysOf(JsonObject obj) =>
        obj.Select(pair => pair.Key).Take(12);

    /// <summary>The resolved value as text: a JSON string as itself, anything else as JSON.</summary>
    internal static string ToText(JsonNode? node) =>
        node is null
            ? "null"
            : node is JsonValue value && value.TryGetValue<string>(out var text)
                // net48 ref assemblies lack [NotNullWhen] on TryGetValue.
                ? text!
                : node.ToJsonString(TextOptions);
}
