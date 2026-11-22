using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the types (FamilySymbol) of one family, including types that are not
/// placed in the model.
/// </summary>
public sealed class ListFamilyTypesTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;
    private const int DefaultParameterLimit = 20;
    private const int MaxParameterLimit = 100;

    public ListFamilyTypesTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_family_types",
        Description =
            "Lists the types (типоразмеры) of a family, including types that are not placed " +
            "in the model. familyId or familyName selects the family; each type returns " +
            "typeId, name, isPlaced and a short parameter list.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""},""familyId"":{""type"":""integer""},""familyIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":50,""description"":""Batch: several families in one call (#471)""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""includeParameters"":{""type"":""boolean"",""default"":true},""parameterLimit"":{""type"":""integer"",""default"":20,""maximum"":100},""includePlaced"":{""type"":""boolean"",""default"":true},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var familyIdsElement = default(JsonElement?);
        if (args.TryGetProperty("familyIds", out var batchElement)
            && batchElement.ValueKind == JsonValueKind.Array
            && batchElement.GetArrayLength() > 0)
        {
            familyIdsElement = batchElement;
        }

        List<Autodesk.Revit.DB.Family> families;
        if (familyIdsElement is not null)
        {
            families = new List<Autodesk.Revit.DB.Family>();
            var missing = new List<long>();
            foreach (var entry in familyIdsElement.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetInt64(out var id))
                    continue;

                if (doc.GetElement(ElementIdCompat.Create(id)) is Autodesk.Revit.DB.Family batchFamily)
                    families.Add(batchFamily);
                else
                    missing.Add(id);
            }

            if (families.Count == 0)
            {
                return ToolResult.Fail(
                    "family_not_found",
                    "None of the familyIds resolved: [" + string.Join(", ", missing) + "].");
            }

            if (missing.Count > 0)
            {
                return ToolResult.Ok(
                    AgentJson.Serialize(new { missing }),
                    "familyIds batch requires every id to resolve; missing: [" +
                    string.Join(", ", missing) + "]. [Action: re-check ids with list_families]");
            }
        }
        else
        {
            var family = AgentFamilyReader.ResolveFamily(
                doc,
                TryGetLong(args, "familyId"),
                AgentJson.GetString(args, "familyName"),
                AgentJson.GetString(args, "familyUniqueId"),
                out var errorCode,
                out var message);
            if (family is null)
                return ToolResult.Fail(errorCode ?? "family_not_found", message ?? "Family not found.");

            families = new List<Autodesk.Revit.DB.Family> { family };
        }

        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);
        var includeParameters = AgentJson.GetBool(args, "includeParameters", true);
        var parameterLimit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "parameterLimit", DefaultParameterLimit),
            DefaultParameterLimit,
            MaxParameterLimit);
        var includePlaced = AgentJson.GetBool(args, "includePlaced", true);

        HashSet<long>? placedTypeIds = null;
        if (includePlaced)
        {
            placedTypeIds = new HashSet<long>();
            var instances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance));
            foreach (var element in instances)
            {
                ct.ThrowIfCancellationRequested();
                if (element is FamilyInstance instance && instance.Symbol is not null)
                    placedTypeIds.Add(instance.Symbol.Id.GetValue());
            }
        }

        var types = new List<FamilySymbol>();
        foreach (var batchFamily in families)
        {
            foreach (var typeId in batchFamily.GetFamilySymbolIds())
            {
                if (doc.GetElement(typeId) is FamilySymbol symbol)
                    types.Add(symbol);
            }
        }

        types.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        var page = AgentPaging.Slice(types, offset, limit);
        var reports = new List<Dictionary<string, object?>>(page.Count);
        foreach (var symbol in page)
        {
            ct.ThrowIfCancellationRequested();
            var report = new Dictionary<string, object?>
            {
                ["typeId"] = symbol.Id.GetValue(),
                ["name"] = symbol.Name,
                ["familyId"] = symbol.Family.Id.GetValue(),
                ["familyName"] = symbol.Family.Name
            };

            if (placedTypeIds is not null)
                report["isPlaced"] = placedTypeIds.Contains(symbol.Id.GetValue());

            if (includeParameters)
            {
                var names = new List<string>();
                foreach (Parameter parameter in symbol.Parameters)
                {
                    if (names.Count >= parameterLimit)
                        break;
                    if (!string.IsNullOrEmpty(parameter.Definition?.Name))
                        names.Add(parameter.Definition!.Name);
                }

                var totalParameters = symbol.Parameters.Size;
                report["parameters"] = names;
                report["parameterCount"] = totalParameters;
                report["parametersTruncated"] = totalParameters > names.Count;
            }

            reports.Add(report);
        }

        var nextOffset = offset + reports.Count;
        var singleFamily = families.Count == 1 ? families[0] : null;
        var payload = new Dictionary<string, object?>
        {
            ["familyId"] = singleFamily?.Id.GetValue(),
            ["familyName"] = singleFamily?.Name,
            ["familyCount"] = families.Count,
            ["category"] = singleFamily?.FamilyCategory?.Name,
            ["types"] = reports,
            ["total"] = types.Count,
            ["returned"] = reports.Count,
            ["offset"] = offset,
            ["nextOffset"] = nextOffset < types.Count ? nextOffset : (int?)null,
            ["truncated"] = nextOffset < types.Count
        };

        var subject = families.Count == 1
            ? "Family '" + families[0].Name + "'"
            : families.Count + " families";
        var messageText = types.Count == 0
            ? subject + " has no types (empty family)."
            : subject + ": " + reports.Count + " of " + types.Count +
              " type(s)" + (nextOffset < types.Count ? ", next offset " + nextOffset : string.Empty) + ".";
        // Live case 5 round 2: reading families one-per-call burned the round
        // budget; nudge the batch parameter for the rest of the list.
        if (families.Count == 1 && !args.TryGetProperty("familyIds", out _))
            messageText += " Reading several families? Pass familyIds:[...] — up to 50 in one call.";

        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
    }

    private static long? TryGetLong(JsonElement args, string property)
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