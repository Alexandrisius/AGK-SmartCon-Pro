using System.Text.Json;

namespace SmartCon.Revit.Agent.Tools;

/// <summary>
/// Finds ElementId-like numeric arguments in a tool payload: keys ending with
/// "id"/"ids" (elementId, viewIds, levelId…), excluding category, workset and
/// parameter ids, whose values are not element ids. Used by the write pipeline
/// to take worksharing ownership before a transaction and by the job engine to
/// know the units' targets.
/// </summary>
internal static class AgentJsonIds
{
    internal static IEnumerable<(string Key, long Id)> Scan(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var property in args.EnumerateObject())
        {
            foreach (var reference in ScanValue(property.Name, property.Value))
                yield return reference;
        }
    }

    internal static IEnumerable<(string Key, long Id)> ScanJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json!);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var reference in Scan(document.RootElement))
                yield return reference;
        }
    }

    /// <summary>Values of keys ending with "uniqueId" (ownerUniqueId, elementUniqueId…).</summary>
    internal static IEnumerable<string> ScanUniqueIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json!);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var value in ScanUniqueValues(document.RootElement))
                yield return value;
        }

        static IEnumerable<string> ScanUniqueValues(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String
                        && property.Name.EndsWith("uniqueid", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        yield return property.Value.GetString()!;
                    }
                    else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        foreach (var nested in ScanUniqueValues(property.Value))
                            yield return nested;
                    }
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    foreach (var nested in ScanUniqueValues(item))
                        yield return nested;
                }
            }
        }
    }

    private static IEnumerable<(string Key, long Id)> ScanValue(string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id))
        {
            if (IsIdKey(key) && id > 0)
                yield return (key, id);
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var reference in ScanValue(key, item))
                    yield return reference;
            }

            yield break;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                foreach (var reference in ScanValue(property.Name, property.Value))
                    yield return reference;
            }
        }
    }

    private static bool IsIdKey(string key)
    {
        var normalized = key.ToLowerInvariant();
        if (normalized.EndsWith("categoryid", StringComparison.Ordinal)
            || normalized.EndsWith("categoryids", StringComparison.Ordinal)
            || normalized.EndsWith("worksetid", StringComparison.Ordinal)
            || normalized.EndsWith("worksetids", StringComparison.Ordinal)
            || normalized.EndsWith("parameterid", StringComparison.Ordinal)
            || normalized.EndsWith("parameterids", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.EndsWith("id", StringComparison.Ordinal)
               || normalized.EndsWith("ids", StringComparison.Ordinal);
    }
}
