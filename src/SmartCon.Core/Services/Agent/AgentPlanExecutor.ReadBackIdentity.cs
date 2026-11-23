using System.Globalization;
using System.Text.Json;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Row identity for the read-back paths: the property that carries a row's
/// own id or name, how byId / byName pick a row by it, and how the node a
/// path got lost in is described to the model.
/// </summary>
public sealed partial class AgentPlanExecutor
{
    /// <summary>
    /// Finds the row whose OWN id equals <paramref name="id"/>, wherever the
    /// payload nests it. A row is matched by its identity property only: an id
    /// it merely references (templateId, levelId…) identifies another object
    /// and used to return the referencing row (review r1 #9).
    /// </summary>
    private static bool TryFindById(
        JsonElement element,
        string id,
        string collectionKey,
        out JsonElement found)
    {
        found = default;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetIdentityId(element, collectionKey, out var identity)
                    && PropertyMatches(identity, id))
                {
                    found = element;
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindById(property.Value, id, property.Name, out found))
                        return true;
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindById(item, id, collectionKey, out found))
                        return true;
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// The property that carries a row's own id: <c>id</c>, else
    /// <c>&lt;singular collection&gt;Id</c> (views → viewId, schedules →
    /// scheduleId — the key list_schedules, list_materials or list_levels rows
    /// identify themselves by), else the first <c>*Id</c> key in document
    /// order. Keys that reference other objects are never chosen while one of
    /// the first two rules applies.
    /// </summary>
    private static bool TryGetIdentityId(JsonElement row, string collectionKey, out JsonElement identity)
    {
        identity = default;
        if (row.ValueKind != JsonValueKind.Object)
            return false;

        var nounId = Singular(collectionKey) + "Id";
        var hasFallback = false;
        foreach (var property in row.EnumerateObject())
        {
            if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, nounId, StringComparison.OrdinalIgnoreCase))
            {
                identity = property.Value;
                return true;
            }

            if (!hasFallback && property.Name.EndsWith("Id", StringComparison.Ordinal))
            {
                identity = property.Value;
                hasFallback = true;
            }
        }

        return hasFallback;
    }

    /// <summary>
    /// "views" → "view", "schedules" → "schedule", "categories" → "category",
    /// "classes" → "class": enough of English plurals to derive the id key a
    /// collection's rows carry (views → viewId). The stem is deliberately
    /// rough: "phases" and "cases" match the "es"-branch (their third-to-last
    /// letter is 's') and come out as "phas"/"cas", so their nounId never
    /// matches — the row is still found through its plain <c>id</c> key or the
    /// first <c>*Id</c> fallback of <see cref="TryGetIdentityId"/>, which is
    /// why the imprecision is acceptable (review r2, N7).
    /// </summary>
    private static string Singular(string key)
    {
        var length = key.Length;
        if (length > 3 && key.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            return key.Substring(0, length - 3) + "y";

        if (length > 2 && key.EndsWith("es", StringComparison.OrdinalIgnoreCase))
        {
            // classes / boxes / matches lose "es"; values (no s/x/z/c/h before
            // the "es") falls through and only loses the "s".
            var before = key[length - 3];
            if (before == 's' || before == 'x' || before == 'z' || before == 'c' || before == 'h')
                return key.Substring(0, length - 2);
        }

        if (length > 1 && key.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && !key.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            return key.Substring(0, length - 1);

        return key;
    }

    /// <summary>
    /// Addresses an object inside an array by its name: <c>parameters.byName["Ширина"]</c>
    /// or <c>valuesByType.byName["Тип 1"]</c>. Matches <c>name</c>/<c>typeName</c>
    /// (and the other *Name identity properties) case-insensitively.
    /// </summary>
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

    private static bool HasNameIdentity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (IsNameProperty(property.Name))
                return true;
        }

        return false;
    }

    /// <summary>
    /// What the read-back payload offers at the node where the path got lost,
    /// so the model can correct the path in one round: the keys of an object or
    /// the ids / names of the rows present.
    /// </summary>
    private static string DescribeNode(JsonElement node, string collectionKey, bool byName)
    {
        var where = collectionKey.Length == 0 ? "the read-back payload" : "'" + collectionKey + "'";
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                if (node.GetArrayLength() == 0)
                    return where + " is empty";

                var present = new List<string>();
                foreach (var item in node.EnumerateArray())
                {
                    if (present.Count >= 5)
                        break;

                    if (TryGetRowIdentity(item, collectionKey, byName, out var value))
                        present.Add(value);
                }

                return where + " has " + node.GetArrayLength().ToString(CultureInfo.InvariantCulture) +
                       " row(s)" +
                       (present.Count == 0
                           ? string.Empty
                           : ", " + (byName ? "names" : "ids") + " present: " + string.Join(", ", present));

            case JsonValueKind.Object:
                if (TryGetRowIdentity(node, collectionKey, byName, out var own))
                    return where + " is a single row" + (byName ? " named '" : " with id '") + own + "'";

                var keys = node.EnumerateObject()
                    .Select(property => property.Name)
                    .Take(8)
                    .ToList();
                var more = node.EnumerateObject().Count() > keys.Count;
                return where + " has keys: " + string.Join(", ", keys) +
                       (more ? ", ..." : string.Empty);

            default:
                return where + " is a " + Kind(node);
        }
    }

    private static bool TryGetRowIdentity(
        JsonElement row,
        string collectionKey,
        bool byName,
        out string value)
    {
        value = string.Empty;
        if (row.ValueKind != JsonValueKind.Object)
            return false;

        if (byName)
        {
            foreach (var property in row.EnumerateObject())
            {
                if (IsNameProperty(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString() ?? string.Empty;
                    return true;
                }
            }

            return false;
        }

        if (!TryGetIdentityId(row, collectionKey, out var identity)
            || identity.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        value = identity.ValueKind == JsonValueKind.String
            ? identity.GetString() ?? string.Empty
            : identity.GetRawText();
        return true;
    }

    private static string Kind(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => node.ValueKind.ToString().ToLowerInvariant()
    };

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
