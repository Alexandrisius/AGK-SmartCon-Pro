using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Outcome of a tolerant argument repair. <see cref="Arguments"/> is the repaired
/// JSON (the original element when <see cref="Changed"/> is false),
/// <see cref="Repairs"/> explains every change in user-facing terms.
/// </summary>
public sealed class AgentToolRepairResult
{
    public required JsonElement Arguments { get; init; }

    public IReadOnlyList<string> Repairs { get; init; } = Array.Empty<string>();

    public bool Changed { get; init; }
}

/// <summary>
/// Tolerant JSON repair applied before schema validation (runbook H1-09).
/// Fixes only unambiguous model mistakes: property aliases (case, snake_case,
/// kebab-case), scalar coercions, single-value/array mismatches, comma-separated
/// strings, single-element object arrays and, when the schema forbids them,
/// unknown properties. Missing required properties are never invented: the
/// validator still reports them, so the model gets an actionable error.
/// </summary>
public static class AgentToolInputRepair
{
    private const int MaxDepth = 32;

    public static AgentToolRepairResult Repair(string schemaJson, JsonElement args)
    {
        var repairs = new List<string>();
        if (args.ValueKind != JsonValueKind.Object)
            return Unchanged(args, repairs);

        JsonDocument schemaDocument;
        try
        {
            schemaDocument = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(schemaJson) ? "{}" : schemaJson);
        }
        catch (JsonException)
        {
            return Unchanged(args, repairs);
        }

        using (schemaDocument)
        {
            var schema = schemaDocument.RootElement;
            if (schema.ValueKind != JsonValueKind.Object
                || !string.Equals(
                    AgentJsonSchema.PrimaryType(schema), "object", StringComparison.Ordinal))
            {
                return Unchanged(args, repairs);
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(args.GetRawText());
            }
            catch (JsonException)
            {
                return Unchanged(args, repairs);
            }

            if (parsed is not JsonObject root)
                return Unchanged(args, repairs);

            var changed = false;
            RepairObject(schema, schema, root, string.Empty, repairs, ref changed, 0);
            if (!changed)
                return Unchanged(args, repairs);

            var repairedJson = root.ToJsonString();
            JsonDocument repairedDocument;
            try
            {
                repairedDocument = JsonDocument.Parse(repairedJson);
            }
            catch (JsonException)
            {
                return Unchanged(args, repairs);
            }

            using (repairedDocument)
            {
                return new AgentToolRepairResult
                {
                    Arguments = repairedDocument.RootElement.Clone(),
                    Repairs = repairs,
                    Changed = true
                };
            }
        }
    }

    private static AgentToolRepairResult Unchanged(
        JsonElement args,
        List<string> repairs) => new()
    {
        Arguments = args.ValueKind == JsonValueKind.Undefined ? args : args.Clone(),
        Repairs = repairs,
        Changed = false
    };

    private static void RepairObject(
        JsonElement root,
        JsonElement schema,
        JsonObject value,
        string path,
        List<string> repairs,
        ref bool changed,
        int depth)
    {
        if (depth > MaxDepth)
            return;

        schema = AgentJsonSchema.Dereference(root, schema);
        var hasProperties = schema.TryGetProperty("properties", out var properties)
                            && properties.ValueKind == JsonValueKind.Object;

        if (hasProperties)
        {
            // Aliases first: a renamed property is then coerced/validated like a
            // property that was spelled correctly from the start.
            foreach (var property in properties.EnumerateObject())
            {
                var name = property.Name;
                if (value.ContainsKey(name))
                    continue;

                var alias = FindAlias(value, name);
                if (alias is null)
                    continue;

                var moved = value[alias];
                value.Remove(alias);
                if (moved is null)
                    continue;

                value[name] = moved;
                repairs.Add(Describe(
                    path, depth, "renamed '" + alias + "' -> '" + name + "'"));
                changed = true;
            }

            foreach (var property in properties.EnumerateObject())
            {
                if (!value.TryGetPropertyValue(property.Name, out var current)
                    || current is null)
                {
                    continue;
                }

                var childPath = JoinPath(path, property.Name);
                value.Remove(property.Name);
                var repaired = RepairValue(
                    root, property.Value, current, childPath, repairs, ref changed, depth + 1);
                if (repaired is not null)
                {
                    value[property.Name] = repaired;
                }
                else
                {
                    changed = true;
                }
            }
        }

        var allowAdditional = !schema.TryGetProperty("additionalProperties", out var additional)
                              || additional.ValueKind != JsonValueKind.False;
        if (allowAdditional)
            return;

        var unknown = new List<string>();
        foreach (var member in value)
        {
            if (hasProperties && properties.TryGetProperty(member.Key, out _))
                continue;

            unknown.Add(member.Key);
        }

        foreach (var key in unknown)
        {
            // documentId is never dropped: the call addressed a background model,
            // so it must fail in the validator instead of silently reading the
            // active document (night run 2, C9-01).
            if (string.IsNullOrEmpty(path)
                && AgentDocumentRoutingArgument.IsName(key))
            {
                continue;
            }

            value.Remove(key);
            repairs.Add(Describe(path, depth, "dropped unknown property '" + key + "'"));
            changed = true;
        }
    }

    private static JsonNode? RepairValue(
        JsonElement root,
        JsonElement schema,
        JsonNode? value,
        string path,
        List<string> repairs,
        ref bool changed,
        int depth)
    {
        if (value is null || depth > MaxDepth)
            return value;

        schema = AgentJsonSchema.Dereference(root, schema);
        value = NormalizeEnum(schema, value, path, repairs, ref changed, depth);

        var type = RepairType(root, schema, value);
        switch (type)
        {
            case "object":
                return RepairObjectValue(root, schema, value, path, repairs, ref changed, depth);
            case "array":
                return RepairArrayValue(root, schema, value, path, repairs, ref changed, depth);
            case "string":
            case "integer":
            case "number":
            case "boolean":
                return CoerceScalar(type, value, path, repairs, ref changed, depth);
            default:
                return value;
        }
    }

    private static string? RepairType(JsonElement root, JsonElement schema, JsonNode? value)
    {
        schema = AgentJsonSchema.Dereference(root, schema);
        var types = AgentJsonSchema.Types(schema);
        if (types.Length == 0)
            return null;
        if (types.Length == 1)
            return types[0];

        foreach (var type in types)
        {
            if (TypeMatches(type, value))
                return type;
        }

        foreach (var type in types)
        {
            if (!string.Equals(type, "null", StringComparison.Ordinal))
                return type;
        }

        return null;
    }

    private static bool TypeMatches(string type, JsonNode? value)
    {
        switch (type)
        {
            case "object":
                return value is JsonObject;
            case "array":
                return value is JsonArray;
            case "null":
                return value is null;
            case "string":
                return value is JsonValue text && text.GetValueKind() == JsonValueKind.String;
            case "number":
                return value is JsonValue number && number.GetValueKind() == JsonValueKind.Number;
            case "integer":
                return value is JsonValue integer
                       && integer.GetValueKind() == JsonValueKind.Number
                       && TryParseInteger(integer.ToJsonString(), out _);
            case "boolean":
                return value is JsonValue flag
                       && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
            default:
                return false;
        }
    }

    private static JsonNode NormalizeEnum(
        JsonElement schema,
        JsonNode value,
        string path,
        List<string> repairs,
        ref bool changed,
        int depth)
    {
        if (value is not JsonValue jsonValue
            || jsonValue.GetValueKind() != JsonValueKind.String
            || !schema.TryGetProperty("enum", out var allowed)
            || allowed.ValueKind != JsonValueKind.Array)
        {
            return value;
        }

        var text = jsonValue.GetValue<string>();
        foreach (var option in allowed.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.String)
                continue;

            var canonical = option.GetString()!;
            if (!string.Equals(canonical, text, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(canonical, text, StringComparison.Ordinal))
                return value;

            repairs.Add(Describe(
                path, depth, "normalized enum \"" + text + "\" -> \"" + canonical + "\""));
            changed = true;
            return JsonValue.Create(canonical)!;
        }

        return value;
    }

    private static JsonNode? RepairObjectValue(
        JsonElement root,
        JsonElement schema,
        JsonNode value,
        string path,
        List<string> repairs,
        ref bool changed,
        int depth)
    {
        if (value is JsonArray single && single.Count == 1)
        {
            var item = single[0];
            single.RemoveAt(0);
            changed = true;
            if (item is null)
                return null;

            value = item;
            repairs.Add(Describe(path, depth, "unwrapped a single-element array into an object"));
        }

        if (value is JsonObject jsonObject)
            RepairObject(root, schema, jsonObject, path, repairs, ref changed, depth);

        return value;
    }

    private static JsonNode? RepairArrayValue(
        JsonElement root,
        JsonElement schema,
        JsonNode value,
        string path,
        List<string> repairs,
        ref bool changed,
        int depth)
    {
        if (value is JsonValue scalar
            && scalar.GetValueKind() == JsonValueKind.String
            && schema.TryGetProperty("items", out var declaredItems)
            && SchemaType(AgentJsonSchema.Dereference(root, declaredItems)) == "string"
            && scalar.GetValue<string>().IndexOf(',') >= 0)
        {
            var parts = scalar.GetValue<string>().Split(',');
            var split = new JsonArray();
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                    split.Add(JsonValue.Create(trimmed));
            }

            if (split.Count > 0)
            {
                value = split;
                repairs.Add(Describe(
                    path, depth, "split a comma-separated string into an array"));
                changed = true;
            }
        }
        else if (value is JsonObject)
        {
            value = new JsonArray(value);
            repairs.Add(Describe(path, depth, "wrapped a single object into an array"));
            changed = true;
        }
        else if (value is JsonValue singleValue
                 && singleValue.GetValueKind() != JsonValueKind.Null)
        {
            value = new JsonArray(singleValue);
            repairs.Add(Describe(path, depth, "wrapped a single value into an array"));
            changed = true;
        }

        if (value is not JsonArray array || !schema.TryGetProperty("items", out var items))
            return value;

        for (var index = 0; index < array.Count; index++)
        {
            var item = array[index];
            if (item is null)
                continue;

            array.RemoveAt(index);
            var repaired = RepairValue(
                root, items, item, path + "[" + index + "]", repairs, ref changed, depth + 1);
            array.Insert(index, repaired);
        }

        return value;
    }

    private static JsonNode CoerceScalar(
        string type,
        JsonNode value,
        string path,
        List<string> repairs,
        ref bool changed,
        int depth)
    {
        if (value is not JsonValue jsonValue)
            return value;

        var kind = jsonValue.GetValueKind();
        switch (type)
        {
            case "string":
                if (kind == JsonValueKind.Number)
                {
                    var text = jsonValue.ToJsonString();
                    repairs.Add(Describe(path, depth, "coerced number " + text + " to string"));
                    changed = true;
                    return JsonValue.Create(text)!;
                }

                if (kind == JsonValueKind.True || kind == JsonValueKind.False)
                {
                    var text = kind == JsonValueKind.True ? "true" : "false";
                    repairs.Add(Describe(path, depth, "coerced boolean " + text + " to string"));
                    changed = true;
                    return JsonValue.Create(text)!;
                }

                break;

            case "integer":
                if (kind == JsonValueKind.String
                    && TryParseInteger(jsonValue.GetValue<string>(), out var integer))
                {
                    repairs.Add(Describe(
                        path, depth,
                        "coerced \"" + jsonValue.GetValue<string>() + "\" to integer"));
                    changed = true;
                    return JsonValue.Create(integer)!;
                }

                break;

            case "number":
                if (kind == JsonValueKind.String
                    && TryParseNumber(jsonValue.GetValue<string>(), out var number))
                {
                    repairs.Add(Describe(
                        path, depth,
                        "coerced \"" + jsonValue.GetValue<string>() + "\" to number"));
                    changed = true;
                    return JsonValue.Create(number)!;
                }

                break;

            case "boolean":
                if (kind == JsonValueKind.String
                    && TryParseBoolean(jsonValue.GetValue<string>(), out var boolean))
                {
                    var text = boolean ? "true" : "false";
                    repairs.Add(Describe(
                        path, depth, "coerced \"" + text + "\" to boolean"));
                    changed = true;
                    return JsonValue.Create(boolean)!;
                }

                break;
        }

        return value;
    }

    private static string? FindAlias(JsonObject value, string name)
    {
        string? caseInsensitive = null;
        string? separated = null;
        foreach (var member in value)
        {
            if (caseInsensitive is null
                && string.Equals(member.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitive = member.Key;
            }

            if (separated is null
                && (member.Key.IndexOf('_') >= 0 || member.Key.IndexOf('-') >= 0))
            {
                var camel = ToCamelCase(member.Key);
                if (string.Equals(camel, name, StringComparison.Ordinal))
                    separated = member.Key;
            }
        }

        return caseInsensitive ?? separated;
    }

    private static string? ToCamelCase(string name)
    {
        if (name.IndexOf('_') < 0 && name.IndexOf('-') < 0)
            return null;

        var parts = name.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var builder = new System.Text.StringBuilder(parts[0].ToLowerInvariant());
        for (var index = 1; index < parts.Length; index++)
        {
            var part = parts[index].ToLowerInvariant();
            if (part.Length == 0)
                continue;

            builder.Append(char.ToUpperInvariant(part[0]));
            builder.Append(part.Substring(1));
        }

        return builder.ToString();
    }

    private static string? SchemaType(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static bool TryParseInteger(string text, out long value)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        if (double.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed)
            && !double.IsInfinity(parsed)
            && parsed == System.Math.Truncate(parsed)
            && parsed >= long.MinValue
            && parsed <= long.MaxValue)
        {
            value = (long)parsed;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryParseNumber(string text, out double value)
    {
        if (double.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        return false;
    }

    private static bool TryParseBoolean(string text, out bool value)
    {
        var trimmed = text.Trim();
        if (bool.TryParse(trimmed, out value))
            return true;

        if (trimmed == "1")
        {
            value = true;
            return true;
        }

        if (trimmed == "0")
        {
            value = false;
            return true;
        }

        return false;
    }

    private static string JoinPath(string path, string name) =>
        string.IsNullOrEmpty(path) ? name : path + "." + name;

    private static string Describe(string path, int depth, string message) =>
        depth > 1 && !string.IsNullOrEmpty(path) ? path + ": " + message : message;
}
