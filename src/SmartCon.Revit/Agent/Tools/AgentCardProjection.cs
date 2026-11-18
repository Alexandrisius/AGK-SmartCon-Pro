using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartCon.Revit.Agent.Tools;

/// <summary>
/// Projects a card object onto the requested fields (mvp-plan §11 "проекции").
/// Aliases keep the model-facing names stable across tools: "type" resolves to
/// viewType/typeName, "level" to levelName, "id" to viewId/elementId. Unknown
/// fields are reported back instead of being dropped silently (live dogfood
/// finding #1).
/// </summary>
internal static class AgentCardProjection
{
    private static readonly Dictionary<string, string[]> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new[] { "viewId", "elementId" },
            ["type"] = new[] { "viewType", "typeName", "type" },
            ["level"] = new[] { "levelName", "level" },
            ["levelid"] = new[] { "levelId" },
            ["template"] = new[] { "templateName", "template" },
            ["templateid"] = new[] { "templateId" },
            ["sheet"] = new[] { "sheetNumber" },
            ["elevation"] = new[] { "elevationMm" },
            ["family"] = new[] { "family" },
            ["category"] = new[] { "category" },
            ["name"] = new[] { "name" },
            ["uniqueid"] = new[] { "uniqueId" },
            ["folder"] = new[] { "browserFolder" },
            ["browserfolder"] = new[] { "browserFolder" },
            ["inbrowser"] = new[] { "inBrowser" }
        };

    internal static JsonElement Apply(
        JsonElement card,
        IReadOnlyList<string>? fields,
        ICollection<string>? ignoredFields = null,
        IDictionary<string, string>? substitutedFields = null)
    {
        if (fields is null || fields.Count == 0)
            return card;

        if (card.ValueKind != JsonValueKind.Object)
            return card;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(card.GetRawText());
        }
        catch (JsonException)
        {
            return card;
        }

        if (node is not JsonObject source)
            return card;

        var projected = new JsonObject();
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field))
                continue;

            if (!TryResolve(source, field, out var property, out var value))
            {
                ignoredFields?.Add(field);
                continue;
            }

            projected[property] = value?.DeepClone();
            if (!string.Equals(field, property, StringComparison.OrdinalIgnoreCase)
                && substitutedFields is not null)
            {
                substitutedFields[field] = property;
            }
        }

        return JsonSerializer.SerializeToElement(projected);
    }

    internal static IReadOnlyList<string> AvailableFields(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();

        try
        {
            var node = JsonNode.Parse(card.GetRawText());
            return node is JsonObject obj
                ? obj.Select(pair => pair.Key).ToList()
                : Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool TryResolve(
        JsonObject source,
        string field,
        out string property,
        out JsonNode? value)
    {
        if (source.TryGetPropertyValue(field, out value))
        {
            property = field;
            return true;
        }

        if (Aliases.TryGetValue(field, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                if (source.TryGetPropertyValue(candidate, out value))
                {
                    property = candidate;
                    return true;
                }
            }
        }

        foreach (var pair in source)
        {
            if (!string.Equals(pair.Key, field, StringComparison.OrdinalIgnoreCase))
                continue;

            property = pair.Key;
            value = pair.Value;
            return true;
        }

        property = field;
        value = null;
        return false;
    }
}
