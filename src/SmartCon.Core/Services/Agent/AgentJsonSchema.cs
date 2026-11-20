using System.Text.Json;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Minimal local JSON Schema support shared by the argument validator and the
/// tolerant repair: local $ref resolution (definitions/$defs) and union types
/// such as ["integer", "null"]. Without it a $ref property was never validated
/// — a typo like offset instead of offsetMm silently produced a wrong view range
/// with success.
/// </summary>
internal static class AgentJsonSchema
{
    private const int MaxRefDepth = 16;

    public static JsonElement Dereference(JsonElement root, JsonElement schema)
    {
        for (var depth = 0; depth < MaxRefDepth; depth++)
        {
            if (schema.ValueKind != JsonValueKind.Object
                || !schema.TryGetProperty("$ref", out var referenceElement)
                || referenceElement.ValueKind != JsonValueKind.String)
            {
                return schema;
            }

            var reference = referenceElement.GetString();
            if (string.IsNullOrEmpty(reference) || reference![0] != '#')
                return schema;

            var path = reference.Substring(1).TrimStart('/');
            if (path.Length == 0)
                return root;

            var current = root;
            foreach (var segment in path.Split('/'))
            {
                var name = segment.Replace("~1", "/").Replace("~0", "~");
                if (current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(name, out var next))
                {
                    return schema;
                }

                current = next;
            }

            schema = current;
        }

        return schema;
    }

    public static string[] Types(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type))
        {
            return Array.Empty<string>();
        }

        if (type.ValueKind == JsonValueKind.String)
            return new[] { type.GetString()! };

        if (type.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var types = new List<string>();
        foreach (var item in type.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                types.Add(item.GetString()!);
        }

        return types.ToArray();
    }

    public static bool Nullable(JsonElement schema) =>
        Array.IndexOf(Types(schema), "null") >= 0;

    public static string? PrimaryType(JsonElement schema)
    {
        foreach (var type in Types(schema))
        {
            if (!string.Equals(type, "null", StringComparison.Ordinal))
                return type;
        }

        return Types(schema).Length == 1 ? "null" : null;
    }
}