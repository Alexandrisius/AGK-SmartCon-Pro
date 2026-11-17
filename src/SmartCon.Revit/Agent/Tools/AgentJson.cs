using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartCon.Revit.Agent.Tools;

internal static class AgentJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions DeserializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static JsonElement Serialize(object value) =>
        JsonSerializer.SerializeToElement(value, SerializerOptions);

    internal static T? Deserialize<T>(JsonElement args)
        where T : class =>
        args.Deserialize<T>(DeserializerOptions);
    internal static string? GetString(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    internal static bool GetBool(JsonElement args, string property, bool fallback)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(property, out var value))
        {
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
        }

        return fallback;
    }

    internal static int GetInt(JsonElement args, string property, int fallback)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    internal static long GetLong(JsonElement args, string property, long fallback)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    internal static IReadOnlyList<string> GetStringArray(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(text!);
            }
        }

        return result;
    }

    internal static IReadOnlyList<long> GetLongArray(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<long>();
        }

        var result = new List<long>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number))
                result.Add(number);
        }

        return result;
    }
}