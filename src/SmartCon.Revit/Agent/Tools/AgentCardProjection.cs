using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartCon.Revit.Agent.Tools;

/// <summary>
/// Projects a card object onto the requested fields (mvp-plan §11 "проекции").
/// Keeps tool results small: list_views/query_elements | fields: [id,name,...].
/// </summary>
internal static class AgentCardProjection
{
    internal static JsonElement Apply(JsonElement card, IReadOnlyList<string>? fields)
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

            if (source.TryGetPropertyValue(field, out var value) && value is not null)
                projected[field] = value.DeepClone();
        }

        return JsonSerializer.SerializeToElement(projected);
    }
}
