using System.Globalization;
using System.Text.Json;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    /// <summary>
    /// Outcome of a read-back path: either a resolved value (including an
    /// explicit JSON null) or an unresolved path with the reason why. An
    /// unresolved path must not be reported as a mismatch — a mismatch with
    /// actual 'null' on an apply that succeeded is exactly the false negative
    /// the live runs hit (C4-10/C9-04).
    /// </summary>
    private sealed class PathResolution
    {
        public bool Resolved { get; }

        public string? Value { get; }

        public string? Reason { get; }

        private PathResolution(bool resolved, string? value, string? reason)
        {
            Resolved = resolved;
            Value = value;
            Reason = reason;
        }

        public static PathResolution Ok(string value) => new(true, value, null);

        public static PathResolution ExplicitNull => new(true, null, null);

        public static PathResolution Unresolved(string reason) => new(false, null, reason);
    }

    private static PathResolution ResolvePath(JsonElement element, string path)
    {
        var current = element;
        foreach (var segment in TokenizePath(path))
        {
            if (segment.Length == 0)
                continue;

            if (TryResolveById(current, segment, out var byId))
            {
                current = byId;
                continue;
            }

            // Flat tool payloads (get_family_parameters.parameters[]) have no id
            // in older answers; parameters.byName["Ширина"] and
            // valuesByType.byName["Тип 1"] are the readable alternative.
            if (TryResolveByName(current, segment, out var byName))
            {
                current = byName;
                continue;
            }

            // "categories[0]" (the form tool output actually shows) as well as the
            // legacy "categories.0" segment. A rejected path reported mismatch on an
            // apply that had actually succeeded (B4).
            if (TryResolveIndexed(current, segment, out var indexed))
            {
                current = indexed;
                continue;
            }

            // A bracketed literal key: values.["Width.2"] keeps a dot inside the
            // key as part of one segment (C4-10).
            if (TryResolveBracketKey(current, segment, out var bracketed))
            {
                current = bracketed;
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

            return PathResolution.Unresolved(
                "path form not supported or no match: segment '" + segment +
                "' does not address the read-back payload (supported: key, key[index], " +
                "[key], byId[id], byName[name])");
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => PathResolution.Ok(current.GetString() ?? string.Empty),
            JsonValueKind.Number => PathResolution.Ok(current.GetRawText()),
            JsonValueKind.True => PathResolution.Ok("true"),
            JsonValueKind.False => PathResolution.Ok("false"),
            JsonValueKind.Null => PathResolution.ExplicitNull,
            _ => PathResolution.Ok(current.GetRawText())
        };
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

    /// <summary>
    /// Addresses an object inside an array by its name: <c>parameters.byName["Ширина"]</c>
    /// or <c>valuesByType.byName["Тип 1"]</c>. Matches <c>name</c>/<c>typeName</c>
    /// (and the other *Name identity properties) case-insensitively.
    /// </summary>
    private static bool TryResolveByName(JsonElement current, string segment, out JsonElement found)
    {
        found = default;
        if (!segment.StartsWith("byName[", StringComparison.Ordinal)
            || !segment.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        var raw = segment.Substring(7, segment.Length - 8).Trim().Trim('"', '\'');
        if (raw.Length == 0)
            return false;

        if (TryFindByName(current, raw, out found))
            return true;

        // Flat value maps (list_family_types values{}) are dictionaries keyed by
        // the parameter name, not arrays of cards: values.byName[param] must
        // address the key itself (C9-04).
        if (current.ValueKind == JsonValueKind.Object
            && current.TryGetProperty(raw, out var mapped))
        {
            found = mapped;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A bracketed literal key segment: <c>values.["Width.2"]</c> addresses an
    /// object key (or an array index) verbatim, so keys containing dots stay
    /// within one segment instead of shattering the path (C4-10).
    /// </summary>
    private static bool TryResolveBracketKey(JsonElement current, string segment, out JsonElement found)
    {
        found = default;
        if (!segment.StartsWith("[", StringComparison.Ordinal)
            || !segment.EndsWith("]", StringComparison.Ordinal)
            || segment.Length < 3)
        {
            return false;
        }

        var raw = segment.Substring(1, segment.Length - 2).Trim().Trim('"', '\'');
        if (raw.Length == 0)
            return false;

        if (current.ValueKind == JsonValueKind.Object
            && current.TryGetProperty(raw, out var property))
        {
            found = property;
            return true;
        }

        if (current.ValueKind == JsonValueKind.Array
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0
            && index < current.GetArrayLength())
        {
            found = current[index];
            return true;
        }

        return false;
    }

    /// <summary>A segment like <c>categories[0]</c>: property name plus array index.</summary>
    private static bool TryResolveIndexed(JsonElement current, string segment, out JsonElement found)
    {
        found = default;
        var open = segment.IndexOf('[');
        if (open <= 0 || !segment.EndsWith("]", StringComparison.Ordinal))
            return false;

        var name = segment.Substring(0, open);
        var indexText = segment.Substring(open + 1, segment.Length - open - 2);
        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            || index < 0)
        {
            return false;
        }

        if (current.ValueKind != JsonValueKind.Object
            || !current.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array
            || index >= array.GetArrayLength())
        {
            return false;
        }

        found = array[index];
        return true;
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
        || string.Equals(name, "levelId", StringComparison.OrdinalIgnoreCase)
        // list_family_types rows identify themselves as typeId (C9-04): without
        // it types.byId[<typeId>] never matched and degraded to actual 'null'.
        || string.Equals(name, "typeId", StringComparison.OrdinalIgnoreCase);

    private static bool TryFindByName(JsonElement element, string name, out JsonElement found)
    {
        found = default;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Match only the object's own identity property. Recursing into
                // valuesByType here would let parameters.byName["Тип 1"] match a
                // parameter that merely contains a type with that name.
                var hasIdentity = false;
                foreach (var property in element.EnumerateObject())
                {
                    if (!IsNameProperty(property.Name))
                        continue;

                    hasIdentity = true;
                    if (NameMatches(property.Value, name))
                    {
                        found = element;
                        return true;
                    }
                }

                if (hasIdentity)
                    return false;

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindByName(property.Value, name, out found))
                        return true;
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindByName(item, name, out found))
                        return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool IsNameProperty(string name) =>
        string.Equals(name, "name", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "typeName", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "familyName", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "viewName", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "parameterName", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "toolName", StringComparison.OrdinalIgnoreCase);

    private static bool NameMatches(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), name, StringComparison.OrdinalIgnoreCase);

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
}
