using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the system types (WallType, FloorType, RoofType, PipeType, DuctType,
/// ...) of one category — the types that do not belong to a loadable family.
/// Loadable family types stay in list_family_types (#441).
/// </summary>
public sealed class ListSystemTypesTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public ListSystemTypesTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_system_types",
        Description =
            "Lists the system types of a category: wall types, floor types, roof types, pipe " +
            "and duct types, cable trays and other ElementType types that do not belong to a " +
            "loadable family (those live in list_family_types). Each row returns typeId, name, " +
            "instanceCount (elements using the type), canBeDuplicated and canBeRenamed — " +
            "duplicate_family_type / rename_family_type / replace_family_type accept these ids " +
            "and names.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""category"":{""description"":""Category name ('Walls', 'OST_Walls', localized) or category id""},""categoryName"":{""type"":""string""},""categoryId"":{""type"":""integer""},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""required"":[""category""],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var category = ResolveCategory(doc, args, out var categoryError);
        if (category is null)
            return ToolResult.Fail("category_not_found", categoryError!);

        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);

        var usage = CountElementsByTypeId(doc, ct);

        var types = new List<ElementType>();
        foreach (var element in new FilteredElementCollector(doc)
                     .WhereElementIsElementType()
                     .OfClass(typeof(ElementType)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not ElementType type || type is FamilySymbol)
                continue;

            if (type.Category is null || type.Category.Id != category.Id)
                continue;

            types.Add(type);
        }

        types.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        var page = AgentPaging.Slice(types, offset, limit);
        var reports = new List<Dictionary<string, object?>>(page.Count);
        foreach (var type in page)
        {
            ct.ThrowIfCancellationRequested();
            usage.TryGetValue(type.Id.GetValue(), out var instances);
            reports.Add(new Dictionary<string, object?>
            {
                ["typeId"] = type.Id.GetValue(),
                ["name"] = type.Name,
                ["instanceCount"] = instances,
                ["canBeDuplicated"] = type.CanBeCopied,
                ["canBeRenamed"] = type.CanBeRenamed
            });
        }

        var nextOffset = offset + reports.Count;
        var payload = new Dictionary<string, object?>
        {
            ["categoryId"] = category.Id.GetValue(),
            ["category"] = category.Name,
            ["types"] = reports,
            ["total"] = types.Count,
            ["returned"] = reports.Count,
            ["offset"] = offset,
            ["nextOffset"] = nextOffset < types.Count ? nextOffset : (int?)null,
            ["truncated"] = nextOffset < types.Count
        };

        var message = types.Count == 0
            ? "Category '" + category.Name + "' has no system types."
            : "Category '" + category.Name + "': " + reports.Count + " of " + types.Count +
              " system type(s)" +
              (nextOffset < types.Count ? ", next offset " + nextOffset : string.Empty) + ".";

        return ToolResult.Ok(AgentJson.Serialize(payload), message);
    }

    private static Category? ResolveCategory(
        Document doc,
        JsonElement args,
        out string? error)
    {
        long? categoryId = null;
        string? categoryName = AgentJson.GetString(args, "categoryName");
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("category", out var combined))
        {
            if (combined.ValueKind == JsonValueKind.String
                && long.TryParse(combined.GetString()!.Trim(), out var parsed))
            {
                categoryId = parsed;
            }
            else if (combined.ValueKind == JsonValueKind.String)
            {
                categoryName = combined.GetString();
            }
            else if (combined.ValueKind == JsonValueKind.Number
                     && combined.TryGetInt64(out var numeric))
            {
                categoryId = numeric;
            }
        }

        categoryId ??= ReadLong(args, "categoryId");
        return AgentFamilyTypeResolver.ResolveCategory(doc, categoryId, categoryName, out error);
    }

    private static Dictionary<long, int> CountElementsByTypeId(Document doc, CancellationToken ct)
    {
        var usage = new Dictionary<long, int>();
        foreach (var element in new FilteredElementCollector(doc)
                     .WhereElementIsNotElementType())
        {
            ct.ThrowIfCancellationRequested();
            var typeId = element.GetTypeId().GetValue();
            if (typeId == -1)
                continue;

            usage.TryGetValue(typeId, out var count);
            usage[typeId] = count + 1;
        }

        return usage;
    }

    private static long? ReadLong(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            return null;
        }

        return result;
    }
}
