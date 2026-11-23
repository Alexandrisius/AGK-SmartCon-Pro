using System.Globalization;
using System.Text.Json;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    /// <summary>
    /// Outcome of resolving one read-back path. The states are kept apart
    /// because they mean different things for the plan (review r1 #2):
    /// <see cref="Resolved"/> — the path addressed a value (an explicit JSON
    /// null included) and it is compared with the expected value;
    /// <see cref="NotFound"/> — the path form is supported but nothing in the
    /// payload matches: the expectation is not confirmed, a mismatch that names
    /// what is missing; <see cref="UnsupportedForm"/> — syntax the resolver does
    /// not understand: nothing was compared, the expectation is not verifiable.
    /// </summary>
    private enum PathStatus
    {
        Resolved,
        NotFound,
        UnsupportedForm
    }

    private sealed class PathResolution
    {
        public PathStatus Status { get; }

        /// <summary>The addressed value; null together with <see cref="Resolved"/> means an explicit JSON null.</summary>
        public string? Value { get; }

        public string? Reason { get; }

        private PathResolution(PathStatus status, string? value, string? reason)
        {
            Status = status;
            Value = value;
            Reason = reason;
        }

        public bool Resolved => Status == PathStatus.Resolved;

        public static PathResolution Of(string value) => new(PathStatus.Resolved, value, null);

        public static PathResolution Null => new(PathStatus.Resolved, null, null);

        public static PathResolution NotFound(string reason) => new(PathStatus.NotFound, null, reason);

        public static PathResolution UnsupportedForm(string reason) =>
            new(PathStatus.UnsupportedForm, null, reason);
    }

    private static PathResolution ResolvePath(JsonElement element, string path)
    {
        var current = element;
        var collectionKey = string.Empty;
        foreach (var segment in TokenizePath(path))
        {
            if (segment.Length == 0)
                continue;

            if (!TryResolveSegment(current, segment, collectionKey, out var next, out var failure))
                return failure!;

            current = next;
            collectionKey = CollectionKeyOf(segment, collectionKey);
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => PathResolution.Of(current.GetString() ?? string.Empty),
            JsonValueKind.Number => PathResolution.Of(current.GetRawText()),
            JsonValueKind.True => PathResolution.Of("true"),
            JsonValueKind.False => PathResolution.Of("false"),
            JsonValueKind.Null => PathResolution.Null,
            _ => PathResolution.Of(current.GetRawText())
        };
    }

    /// <summary>
    /// The key of the collection the current node came from ("views" for
    /// <c>views</c> and <c>views[2]</c>): it tells <c>byId</c> which property
    /// carries a row's own id. <c>byId</c>/<c>byName</c> are selectors, not
    /// collections: the row they pick still belongs to the previous collection,
    /// so that key is kept for the diagnostics and the identity lookup
    /// (review r2, N5).
    /// </summary>
    private static string CollectionKeyOf(string segment, string previous)
    {
        var open = segment.IndexOf('[');
        if (open < 0)
            return segment;

        if (open == 0)
            return previous;

        var prefix = segment.Substring(0, open);
        return string.Equals(prefix, "byId", StringComparison.Ordinal)
               || string.Equals(prefix, "byName", StringComparison.Ordinal)
            ? previous
            : prefix;
    }

    private static bool TryResolveSegment(
        JsonElement current,
        string segment,
        string collectionKey,
        out JsonElement found,
        out PathResolution? failure)
    {
        found = default;
        failure = null;
        var open = segment.IndexOf('[');
        if (open < 0)
            return TryResolveKey(current, segment, collectionKey, out found, out failure);

        var closes = segment.EndsWith("]", StringComparison.Ordinal);
        if (!closes)
        {
            // A key may legitimately contain an unbalanced bracket ("x[y]z"):
            // honour it when it exists verbatim, otherwise the form is unknown.
            if (open > 0
                && current.ValueKind == JsonValueKind.Object
                && current.TryGetProperty(segment, out var verbatim))
            {
                found = verbatim;
                return true;
            }

            failure = PathResolution.UnsupportedForm(
                "segment '" + segment + "' has unbalanced brackets");
            return false;
        }

        var inner = segment.Substring(open + 1, segment.Length - open - 2).Trim().Trim('"', '\'');

        // Selectors pick a row out of the current node.
        if (segment.StartsWith("byId[", StringComparison.Ordinal))
        {
            if (inner.Length == 0)
            {
                failure = PathResolution.UnsupportedForm("segment '" + segment + "' has an empty byId selector");
                return false;
            }

            if (TryFindById(current, inner, collectionKey, out found))
                return true;

            failure = PathResolution.NotFound(
                "no row with id '" + inner + "': " + DescribeNode(current, collectionKey, byName: false));
            return false;
        }

        if (segment.StartsWith("byName[", StringComparison.Ordinal))
        {
            if (inner.Length == 0)
            {
                failure = PathResolution.UnsupportedForm("segment '" + segment + "' has an empty byName selector");
                return false;
            }

            if (TryFindByName(current, inner, out found))
                return true;

            // Flat tool payloads (list_family_types values{}) are dictionaries
            // keyed by the parameter name, not arrays of cards: values.byName[param]
            // addresses the key itself (C9-04). Only for objects without an
            // identity of their own — a row named X must never be matched
            // through one of its field keys (review r1 #6).
            if (current.ValueKind == JsonValueKind.Object
                && !HasNameIdentity(current)
                && current.TryGetProperty(inner, out var mapped))
            {
                found = mapped;
                return true;
            }

            failure = PathResolution.NotFound(
                "no row named '" + inner + "': " + DescribeNode(current, collectionKey, byName: true));
            return false;
        }

        if (open > 0)
        {
            var name = segment.Substring(0, open);

            // "categories[0]": property name plus array index.
            if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0)
            {
                return TryResolveIndex(current, name, index, collectionKey, out found, out failure);
            }

            if (current.ValueKind == JsonValueKind.Object
                && current.TryGetProperty(segment, out var literalKey))
            {
                found = literalKey;
                return true;
            }

            failure = PathResolution.UnsupportedForm(
                "segment '" + segment + "': '" + inner + "' is not an array index — select a row " +
                "by name with byName[name] or address a key verbatim with [key]");
            return false;
        }

        // "[key]": a literal object key (or an array index) — the form that
        // keeps dots inside a key (C4-10).
        if (inner.Length == 0)
        {
            failure = PathResolution.UnsupportedForm("segment '" + segment + "' has an empty [] selector");
            return false;
        }

        if (current.ValueKind == JsonValueKind.Object
            && current.TryGetProperty(inner, out var property))
        {
            found = property;
            return true;
        }

        if (current.ValueKind == JsonValueKind.Array
            && int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bracketIndex)
            && bracketIndex >= 0
            && bracketIndex < current.GetArrayLength())
        {
            found = current[bracketIndex];
            return true;
        }

        failure = PathResolution.NotFound(
            "no key '" + inner + "': " + DescribeNode(current, collectionKey, byName: false));
        return false;
    }

    /// <summary>A plain key of an object, or a numeric index of an array.</summary>
    private static bool TryResolveKey(
        JsonElement current,
        string segment,
        string collectionKey,
        out JsonElement found,
        out PathResolution? failure)
    {
        found = default;
        failure = null;
        switch (current.ValueKind)
        {
            case JsonValueKind.Object:
                if (current.TryGetProperty(segment, out var property))
                {
                    found = property;
                    return true;
                }

                failure = PathResolution.NotFound(
                    "no key '" + segment + "': " + DescribeNode(current, collectionKey, byName: false));
                return false;

            case JsonValueKind.Array:
                if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 0
                    && index < current.GetArrayLength())
                {
                    found = current[index];
                    return true;
                }

                failure = PathResolution.NotFound(
                    "segment '" + segment + "' is not an index of " +
                    DescribeNode(current, collectionKey, byName: false));
                return false;

            default:
                failure = PathResolution.NotFound(
                    "the value addressed before '" + segment + "' is a " + Kind(current) +
                    ", not an object or an array");
                return false;
        }
    }

    /// <summary>A segment like <c>categories[0]</c>: property name plus array index.</summary>
    private static bool TryResolveIndex(
        JsonElement current,
        string name,
        int index,
        string collectionKey,
        out JsonElement found,
        out PathResolution? failure)
    {
        found = default;
        failure = null;
        if (current.ValueKind != JsonValueKind.Object)
        {
            failure = PathResolution.NotFound(
                "the value addressed before '" + name + "[" + index + "]' is a " + Kind(current) +
                ", not an object");
            return false;
        }

        if (!current.TryGetProperty(name, out var array))
        {
            failure = PathResolution.NotFound(
                "no key '" + name + "': " + DescribeNode(current, collectionKey, byName: false));
            return false;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            failure = PathResolution.NotFound(
                "key '" + name + "' is a " + Kind(array) + ", not an array");
            return false;
        }

        if (index >= array.GetArrayLength())
        {
            failure = PathResolution.NotFound(
                "index " + index + " is out of range of '" + name + "': " +
                DescribeNode(array, name, byName: false));
            return false;
        }

        found = array[index];
        return true;
    }

    /// <summary>
    /// Splits a read-back path into dot segments while keeping bracketed parts
    /// intact: "views.byName[View 1.2].templateName" is three segments because
    /// the dot inside the brackets belongs to the name key. A naive
    /// Split('.') shattered such keys and resolved the path to null (C4-10).
    /// </summary>
    private static List<string> TokenizePath(string path)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var character in path)
        {
            switch (character)
            {
                case '[':
                    depth++;
                    current.Append(character);
                    break;
                case ']':
                    if (depth > 0)
                        depth--;
                    current.Append(character);
                    break;
                case '.' when depth == 0:
                    if (current.Length > 0)
                    {
                        segments.Add(current.ToString());
                        current.Clear();
                    }

                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        if (current.Length > 0)
            segments.Add(current.ToString());

        return segments;
    }
}
