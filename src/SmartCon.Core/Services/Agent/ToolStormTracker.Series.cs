using System.Text;
using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// The item-by-item half of the tracker: a batch tool fed the objects of one
/// job one per call, with everything else in the call unchanged.
/// </summary>
public sealed partial class ToolStormTracker
{
    public const int ItemByItemThreshold = 3;
    public const int ItemByItemWindowRounds = 12;

    private readonly Dictionary<string, ItemByItemSeries> _itemSeries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BatchShape?> _shapes = new(StringComparer.Ordinal);

    private void ObserveItemByItem(
        IToolRegistry? registry,
        string toolName,
        string? argumentsJson,
        int round,
        List<string> hints)
    {
        var shape = registry is null ? null : Shape(registry, toolName);
        if (shape is null)
            return;

        // A plan is applied by repeating the previewed call, so single-step
        // plans always come in pairs: that is the plan contract, not a storm.
        if (AgentToolPacks.SystemToolNames.Contains(toolName)
            && !string.Equals(toolName, AgentToolPacks.LoadToolPackName, StringComparison.Ordinal))
        {
            return;
        }

        var call = SingleObjectCall(argumentsJson, shape);
        if (call is null)
        {
            // A batch, or a call without the tool's object list: the walk is over.
            _itemSeries.Remove(toolName);
            return;
        }

        if (!_itemSeries.TryGetValue(toolName, out var series) || series.Rest != call.Rest)
        {
            // The rest of the call changed, so these calls are not one job fed
            // one object at a time.
            series = new ItemByItemSeries { Rest = call.Rest };
            _itemSeries[toolName] = series;
        }

        series.Rounds.Add(round);
        series.Objects.Add(call.Objects);
        if (series.Rounds.Count < ItemByItemThreshold
            || round - series.Rounds[series.Rounds.Count - ItemByItemThreshold] > ItemByItemWindowRounds)
        {
            return;
        }

        // The same object again is a repeat, which the identical-call rule names.
        var last = series.Objects.Skip(series.Objects.Count - ItemByItemThreshold).ToList();
        if (last.Distinct(StringComparer.Ordinal).Count() < ItemByItemThreshold)
            return;

        hints.Add(
            "Single-object calls of " + toolName + " in round(s) " +
            Join(series.Rounds.Skip(series.Rounds.Count - ItemByItemThreshold)) +
            ": this tool takes several items in one call; batch them.");
        _itemSeries.Remove(toolName);
    }

    /// <summary>
    /// The call hands the tool exactly one object — or slices its batch down to
    /// one unit — and everything else in the arguments is the "rest" a series
    /// of such calls is compared by. Null for a batch, for a bound above one,
    /// and for a call that names no object at all.
    /// </summary>
    private static OneObjectCall? SingleObjectCall(string? argumentsJson, BatchShape shape)
    {
        // net48 ref assemblies carry no [NotNullWhen] on IsNullOrWhiteSpace.
        var json = argumentsJson ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var objects = new StringBuilder();
            var rest = new StringBuilder();
            var addresses = 0;
            var restCount = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!shape.Addresses(property.Name, out var isBound))
                {
                    AppendMember(rest, property, restCount == 0);
                    restCount++;
                    continue;
                }

                if (ObjectCount(property.Value, isBound) != 1)
                    return null;

                AppendMember(objects, property, addresses == 0);
                addresses++;
            }

            return addresses == 0
                ? null
                : new OneObjectCall(objects.ToString(), rest.ToString());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ObjectCount(JsonElement value, bool isBound) =>
        isBound
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bound) ? (int)bound : -1
            : value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : -1;

    private static void AppendMember(StringBuilder builder, JsonProperty property, bool first)
    {
        if (!first)
            builder.Append(',');

        builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
        WriteCanonical(property.Value, builder);
    }

    /// <summary>
    /// JSON without whitespace and with object keys sorted: two spellings of
    /// one argument set compare equal.
    /// </summary>
    private static void WriteCanonical(JsonElement value, StringBuilder builder)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var members = value.EnumerateObject()
                    .OrderBy(member => member.Name, StringComparer.Ordinal)
                    .ToList();
                builder.Append('{');
                for (var index = 0; index < members.Count; index++)
                {
                    AppendMember(builder, members[index], index == 0);
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first)
                        builder.Append(',');

                    first = false;
                    WriteCanonical(item, builder);
                }

                builder.Append(']');
                break;

            default:
                builder.Append(value.GetRawText());
                break;
        }
    }

    /// <summary>
    /// The arguments through which one call takes its objects, read once from
    /// the tool schema: "items" and the id/name address lists ("viewIds",
    /// "familyNames") that are not exclusions, plus the job engine's batch
    /// bounds ("batchSize", "maxItems"). Any other array or bound may be a
    /// filter, a projection or a page size — one condition or a page of one
    /// line is not a storm.
    /// </summary>
    private BatchShape? Shape(IToolRegistry registry, string toolName)
    {
        if (_shapes.TryGetValue(toolName, out var cached))
            return cached;

        BatchShape? shape = null;
        if (registry.TryGet(toolName, out var tool) && tool is not null)
        {
            try
            {
                var json = tool.Definition.InputSchemaJson;
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(json) ? "{}" : json);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("properties", out var properties)
                    && properties.ValueKind == JsonValueKind.Object)
                {
                    var lists = new List<string>();
                    var bounds = new List<string>();
                    foreach (var property in properties.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Object
                            || !property.Value.TryGetProperty("type", out var type)
                            || type.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var kind = type.GetString();
                        if (string.Equals(kind, "array", StringComparison.Ordinal)
                            && IsObjectList(property.Name))
                        {
                            lists.Add(property.Name);
                        }
                        else if (string.Equals(kind, "integer", StringComparison.Ordinal)
                                 && IsBatchBound(property.Name))
                        {
                            bounds.Add(property.Name);
                        }
                    }

                    if (lists.Count > 0 || bounds.Count > 0)
                        shape = new BatchShape(lists.ToArray(), bounds.ToArray());
                }
            }
            catch (JsonException)
            {
                shape = null;
            }
        }

        _shapes[toolName] = shape;
        return shape;
    }

    private static bool IsObjectList(string name)
    {
        foreach (var prefix in ExclusionPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return string.Equals(name, "items", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("Names", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBatchBound(string name) =>
        string.Equals(name, "batchSize", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "maxItems", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ExclusionPrefixes = { "except", "exclude", "skip", "without" };

    private sealed class ItemByItemSeries
    {
        public string Rest { get; init; } = string.Empty;

        public List<int> Rounds { get; } = new();

        public List<string> Objects { get; } = new();
    }

    private sealed class OneObjectCall
    {
        public OneObjectCall(string objects, string rest)
        {
            Objects = objects;
            Rest = rest;
        }

        public string Objects { get; }

        public string Rest { get; }
    }

    private sealed class BatchShape
    {
        public BatchShape(string[] lists, string[] bounds)
        {
            Lists = lists;
            Bounds = bounds;
        }

        public string[] Lists { get; }

        public string[] Bounds { get; }

        public bool Addresses(string name, out bool isBound)
        {
            isBound = Bounds.Contains(name, StringComparer.Ordinal);
            return isBound || Lists.Contains(name, StringComparer.Ordinal);
        }
    }
}
