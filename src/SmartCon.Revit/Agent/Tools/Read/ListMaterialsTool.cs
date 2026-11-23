using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the material elements of the project. Deliberately cheap: no usage
/// scan (counting the elements of every material means walking the whole
/// model); where a material is used is answered by query_elements with a
/// material parameter condition.
/// </summary>
public sealed class ListMaterialsTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public ListMaterialsTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_materials",
        Description =
            "Lists the materials (материалы) of the project: materialId, name, materialClass " +
            "(e.g. Wood, Metal, Concrete), category, color as #RRGGBB, transparency and " +
            "shininess. Filter: nameContains. Paged: limit (default 200, max 500), offset, " +
            "returned, nextOffset, truncated. fields=[\"materialId\",\"name\"] projects each " +
            "card onto the listed properties; unknown names are reported in ignoredFields with " +
            "availableFields. The list does not count usage — find where a material is applied " +
            "with query_elements (a material parameter condition). Materials are elements: " +
            "set_parameters writes them by materialId.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""nameContains"":{""type"":""string""},""fields"":{""type"":""array"",""description"":""Allowed: materialId,id,name,uniqueId,materialClass,class,category,color,transparency,shininess. Returned keys match the requested names."",""items"":{""type"":""string""}},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var nameContains = AgentJson.GetString(args, "nameContains");
        var fields = AgentJson.GetStringArray(args, "fields");
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit), DefaultLimit, MaxLimit);

        var matched = new List<MaterialCard>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(Material)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not Material material)
                continue;

            var card = CreateCard(material);
            if (!Contains(card.Name, nameContains))
                continue;

            matched.Add(card);
        }

        matched.Sort((left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : left.MaterialId.CompareTo(right.MaterialId);
        });

        var total = matched.Count;
        var page = AgentPaging.Slice(matched, offset, limit);
        var hasMore = offset + page.Count < total;

        if (fields.Count > 0)
        {
            var projected = new List<JsonElement>(page.Count);
            var ignored = new List<string>();
            var substituted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var card in page)
            {
                projected.Add(AgentCardProjection.Apply(
                    AgentJson.SerializeCard(card), fields, ignored, substituted));
            }

            var available = page.Count > 0
                ? AgentCardProjection.AvailableFields(AgentJson.SerializeCard(page[0]))
                : Array.Empty<string>();

            var projectedPayload = new
            {
                total,
                offset,
                returned = projected.Count,
                nextOffset = hasMore ? offset + projected.Count : (int?)null,
                truncated = hasMore,
                ignoredFields = ignored.Count > 0 ? ignored : null,
                substitutedFields = substituted.Count > 0 ? substituted : null,
                availableFields = ignored.Count > 0 ? available : null,
                materials = projected
            };

            return ToolResult.Ok(
                AgentJson.Serialize(projectedPayload),
                Message(total, projected.Count, hasMore));
        }

        var payload = new
        {
            total,
            offset,
            returned = page.Count,
            nextOffset = hasMore ? offset + page.Count : (int?)null,
            truncated = hasMore,
            materials = page
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            Message(total, page.Count, hasMore));
    }

    private static string Message(int total, int returned, bool hasMore) =>
        "Matched " + total + " material(s); returned " + returned + "." +
        (hasMore
            ? " The result is truncated — pass nextOffset as offset to read the next page."
            : string.Empty);

    private static MaterialCard CreateCard(Material material)
    {
        var color = material.Color;
        return new MaterialCard
        {
            MaterialId = material.Id.GetValue(),
            UniqueId = material.UniqueId,
            Name = material.Name ?? string.Empty,
            MaterialClass = material.MaterialClass,
            Category = material.MaterialCategory,
            Color = color is null
                ? null
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "#{0:X2}{1:X2}{2:X2}",
                    color.Red,
                    color.Green,
                    color.Blue),
            Transparency = material.Transparency,
            Shininess = material.Shininess
        };
    }

    private static bool Contains(string? value, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;
        if (string.IsNullOrEmpty(value))
            return false;

#if NET8_0_OR_GREATER
        return value.Contains(needle, StringComparison.OrdinalIgnoreCase);
#else
        return value!.IndexOf(needle!, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }

    private sealed class MaterialCard
    {
        public long MaterialId { get; set; }

        public string UniqueId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? MaterialClass { get; set; }

        public string? Category { get; set; }

        public string? Color { get; set; }

        public int Transparency { get; set; }

        public int Shininess { get; set; }
    }
}
