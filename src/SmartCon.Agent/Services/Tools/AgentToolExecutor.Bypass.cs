using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed partial class AgentToolExecutor
{
    private static int CountItems(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return 0;

        if (args.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items.GetArrayLength();

        if (args.TryGetProperty("targetViewIds", out var targets)
            && targets.ValueKind == JsonValueKind.Array)
        {
            return targets.GetArrayLength();
        }

        if (args.TryGetProperty("mapping", out var mapping) && mapping.ValueKind == JsonValueKind.Array)
        {
            var count = mapping.GetArrayLength();
            if (args.TryGetProperty("filter", out var filter)
                && filter.ValueKind == JsonValueKind.Object
                && filter.TryGetProperty("limit", out var limit)
                && limit.TryGetInt32(out var parsedLimit))
            {
                count = Math.Max(count, parsedLimit);
            }

            return count;
        }

        return 0;
    }

    private int CountAffected(AgentToolDefinition definition, JsonElement args)
    {
        if (string.Equals(definition.Name, "execute_plan", StringComparison.Ordinal))
            return CountPlanAffected(args);

        return EstimateAffected(definition.Name, args);
    }

    /// <summary>
    /// Conservative change estimate for a whole plan, independent of whether the
    /// plan is safe enough for auto-approval: the confirmation dialog must show a
    /// real number even for destructive or unknown steps.
    /// </summary>
    private int CountPlanAffected(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var total = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object)
            {
                total++;
                continue;
            }

            var stepAffected = 0;
            if (step.TryGetProperty("toolName", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                && step.TryGetProperty("arguments", out var arguments))
            {
                stepAffected = EstimateAffected(
                    nameElement.GetString() ?? string.Empty, arguments);
            }

            if (step.TryGetProperty("expectedChanges", out var expected)
                && expected.ValueKind == JsonValueKind.Number
                && expected.TryGetInt32(out var expectedCount)
                && expectedCount > stepAffected)
            {
                stepAffected = expectedCount;
            }

            total += Math.Max(1, stepAffected);
        }

        return total;
    }

    /// <summary>
    /// Conservative affected-count estimate for the bypass guard: one item may
    /// produce several changes (sections, hidden categories, remapped values),
    /// so the guard must not use the raw item-array length.
    /// </summary>
    private static int EstimateAffected(string toolName, JsonElement args)
    {
        switch (toolName)
        {
            case "configure_views":
                return CountArray(args, "items") * ConfigureSectionsPerItem(args);
            case "copy_view_settings":
                return CountArray(args, "targetViewIds") * CopySections(args);
            case "remap_parameter_values":
                return CountArray(args, "mapping") * RemapLimit(args);
            case "set_view_visibility":
                return VisibilityChanges(args);
            default:
                return CountItems(args);
        }
    }

    private static int CountArray(JsonElement args, string property) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static int ConfigureSectionsPerItem(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return 1;
        }

        var max = 1;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var sections = 0;
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name is "newName" or "templateId" or "viewRange" or "crop"
                    or "visibility")
                {
                    sections++;
                }
            }

            if (sections > max)
                max = sections;
        }

        return max;
    }

    private static int CopySections(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("sections", out var sections)
            || sections.ValueKind != JsonValueKind.Array)
        {
            return 1;
        }

        var count = 0;
        foreach (var section in sections.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.String)
                continue;

            if (string.Equals(section.GetString(), "all", StringComparison.OrdinalIgnoreCase))
                return 4;

            count++;
        }

        return Math.Max(1, count);
    }

    private static int RemapLimit(JsonElement args)
    {
        var limit = 500;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("filter", out var filter)
            && filter.ValueKind == JsonValueKind.Object
            && filter.TryGetProperty("limit", out var limitElement)
            && limitElement.TryGetInt32(out var parsed))
        {
            limit = Math.Max(1, Math.Min(parsed, 500));
        }

        return limit;
    }

    private static int VisibilityChanges(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var total = 0;
        foreach (var item in items.EnumerateArray())
        {
            var perView = 0;
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("visibility", out var visibility)
                && visibility.ValueKind == JsonValueKind.Object)
            {
                perView += CountArray(visibility, "categories");
                perView += CountArray(visibility, "worksets");
            }

            total += Math.Max(1, perView);
        }

        return total;
    }

    /// <summary>
    /// Bypass guard for <c>execute_plan</c>: a plan is auto-approved only when
    /// every step resolves to a known non-destructive tool and the summed batch
    /// stays under the bypass limit. Nested plans and allowDestructive always
    /// require explicit confirmation.
    /// </summary>
    private bool TryGetPlanRisk(
        JsonElement args,
        out AgentToolRisk risk,
        out int affected)
    {
        risk = AgentToolRisk.Read;
        affected = 0;

        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        if (args.TryGetProperty("allowDestructive", out var allow)
            && allow.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object
                || !step.TryGetProperty("toolName", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var name = nameElement.GetString() ?? string.Empty;
            if (!_registry.TryGet(name, out var tool) || tool is null)
                return false;

            if (string.Equals(
                    tool.Definition.Name, "execute_plan", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    tool.Definition.Name, "discard_change_set",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (tool.Definition.Risk == AgentToolRisk.Destructive)
                return false;

            if (tool.Definition.Risk > risk)
                risk = tool.Definition.Risk;

            var stepAffected = 0;
            if (step.TryGetProperty("arguments", out var arguments))
                stepAffected = EstimateAffected(tool.Definition.Name, arguments);

            if (step.TryGetProperty("expectedChanges", out var expected)
                && expected.ValueKind == JsonValueKind.Number
                && expected.TryGetInt32(out var expectedCount)
                && expectedCount > stepAffected)
            {
                stepAffected = expectedCount;
            }

            affected += stepAffected;
        }

        return true;
    }
}
