using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    /// <summary>
    /// Short listing of what the tool actually counted as changes, so the model
    /// can correct expectedChanges on the first try instead of guessing the
    /// tool's counting rule (C7-04).
    /// </summary>
    private static string DescribeCountedItems(ToolResult result, int max = 3)
    {
        if (result.Data is not { ValueKind: JsonValueKind.Object } data
            || !data.TryGetProperty("changed", out var changed)
            || changed.ValueKind != JsonValueKind.Array
            || changed.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var described = new List<string>();
        foreach (var item in changed.EnumerateArray())
        {
            if (described.Count >= max)
                break;

            described.Add(DescribeChangedItem(item));
        }

        var rest = changed.GetArrayLength() - described.Count;
        return ": " + string.Join("; ", described)
               + (rest > 0 ? "; +" + rest + " more" : string.Empty);
    }

    private static string DescribeChangedItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return item.ValueKind == JsonValueKind.String
                ? "'" + item.GetString() + "'"
                : item.GetRawText();

        var label = FirstStringProperty(item, "name", "viewName", "parameterName",
            "familyName", "typeName", "scheduleName", "fieldName");
        var action = FirstStringProperty(item, "action", "status", "detail");
        var id = FirstStringProperty(item, "id", "viewId", "elementId");

        var parts = new List<string>(3);
        if (label is not null)
            parts.Add("'" + label + "'");
        if (action is not null && !string.Equals(action, label, StringComparison.Ordinal))
            parts.Add(action);
        if (parts.Count == 0 && id is not null)
            parts.Add("id " + id);

        return parts.Count switch
        {
            0 => "(unnamed)",
            1 => parts[0],
            _ => parts[0] + " (" + string.Join(", ", parts.Skip(1)) + ")"
        };
    }

    private static string? FirstStringProperty(JsonElement item, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (item.TryGetProperty(property, out var value))
            {
                if (value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
                if (value.ValueKind == JsonValueKind.Number)
                    return value.GetRawText();
            }
        }

        return null;
    }

    /// <summary>
    /// Names the objects (and real ids) the step's apply actually created, so a
    /// read-back that failed on a stale dry-run id points at the live id
    /// instead of leaving the model to guess (ORCH-6: a preview scheduleId was
    /// rolled back and the read-back reported schedule_not_found).
    /// </summary>
    private static string DescribeAppliedCreatedIds(ToolResult? applyResult, int max = 3)
    {
        if (applyResult?.Data is not { ValueKind: JsonValueKind.Object } data)
            return string.Empty;

        var described = new List<string>();
        foreach (var property in data.EnumerateObject())
        {
            if (!property.Name.StartsWith("created", StringComparison.Ordinal)
                || described.Count >= max)
            {
                continue;
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    AddCreatedDescription(property.Value, described);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                            AddCreatedDescription(item, described);
                    }

                    break;
            }
        }

        if (described.Count == 0)
            return string.Empty;

        return "; the step's apply created " + string.Join("; ", described) +
               " — dry-run ids were rolled back, address the applied object by name or id";
    }

    private static void AddCreatedDescription(JsonElement created, List<string> described)
    {
        if (described.Count >= 3)
            return;

        var name = FirstStringProperty(created, "name", "scheduleName", "viewName", "familyName");
        var id = FirstStringProperty(created, "id", "scheduleId", "viewId", "familyId", "typeId");
        if (name is null && id is null)
            return;

        described.Add("'" + (name ?? "(unnamed)") + "'" + (id is null ? string.Empty : " (id " + id + ")"));
    }
}
