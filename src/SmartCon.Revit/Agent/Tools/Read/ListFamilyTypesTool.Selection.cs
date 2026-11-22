using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed partial class ListFamilyTypesTool
{
    private static ToolResult? ResolveDirectTypes(
        Document doc,
        IReadOnlyList<long> requestedTypeIds,
        out List<FamilySymbol> types)
    {
        types = new List<FamilySymbol>();
        if (requestedTypeIds.Count == 0)
            return null;

        var missing = new List<long>();
        var seen = new HashSet<long>();
        foreach (var id in requestedTypeIds)
        {
            if (!seen.Add(id))
                continue;

            if (doc.GetElement(ElementIdCompat.Create(id)) is FamilySymbol symbol)
                types.Add(symbol);
            else
                missing.Add(id);
        }

        if (missing.Count == 0)
            return null;

        return ToolResult.Fail(
            "type_not_found",
            (types.Count == 0 ? "None of the typeIds resolved" : "typeIds requires every id to resolve") +
            "; missing: [" + string.Join(", ", missing) + "]. " +
            "[Action: re-read the family with list_family_types and use the typeId it returns]",
            data: AgentJson.Serialize(new { missing }));
    }

    private static ToolResult? ResolveFamilies(
        Document doc,
        JsonElement args,
        bool hasDirectTypes,
        out List<Autodesk.Revit.DB.Family> families,
        out bool isBatch)
    {
        families = new List<Autodesk.Revit.DB.Family>();
        isBatch = args.TryGetProperty("familyIds", out var batchElement)
                  && batchElement.ValueKind == JsonValueKind.Array
                  && batchElement.GetArrayLength() > 0;
        if (isBatch)
        {
            var missing = new List<long>();
            foreach (var entry in batchElement.EnumerateArray())
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

            return null;
        }

        var familyId = TryGetLong(args, "familyId");
        var familyName = AgentJson.GetString(args, "familyName");
        var familyUniqueId = AgentJson.GetString(args, "familyUniqueId");
        var hasFamilySelector = familyId.HasValue
                                || !string.IsNullOrWhiteSpace(familyName)
                                || !string.IsNullOrWhiteSpace(familyUniqueId);
        if (!hasFamilySelector)
        {
            return hasDirectTypes
                ? null
                : ToolResult.Fail(
                    "invalid_arguments",
                    "Pass familyId, familyUniqueId, familyName or typeIds.");
        }

        var family = AgentFamilyReader.ResolveFamily(
            doc, familyId, familyName, familyUniqueId, out var errorCode, out var message);
        if (family is null)
            return ToolResult.Fail(errorCode ?? "family_not_found", message ?? "Family not found.");

        families.Add(family);
        return null;
    }

    private static ToolResult? ResolveFields(
        JsonElement args,
        bool includePlaced,
        bool includeParameters,
        out AgentListPayload.FieldSelection selection,
        out List<string> fields)
    {
        selection = AgentListPayload.SelectFields(
            AvailableFields,
            AgentJson.GetStringArray(args, "fields"),
            DefaultFields);
        fields = selection.Fields.ToList();
        if (!selection.IsValid)
            return ToolResult.Fail("invalid_arguments", selection.Error!);

        if (selection.IsDefault)
        {
            // An explicitly passed old switch keeps its field in the compact record.
            if (IsTrue(args, "includePlaced"))
                AppendField(fields, "isPlaced");
            if (IsTrue(args, "includeParameters"))
            {
                AppendField(fields, "parameters");
                AppendField(fields, "parameterCount");
            }
        }

        if (!includePlaced)
            fields.Remove("isPlaced");
        if (!includeParameters)
        {
            fields.Remove("parameters");
            fields.Remove("parameterCount");
        }

        return null;
    }

    private static List<string> FullRecordFields(bool includePlaced, bool includeParameters)
    {
        var fields = new List<string> { "typeId", "name", "familyId", "familyName" };
        if (includePlaced)
            fields.Add("isPlaced");
        if (includeParameters)
        {
            fields.Add("parameters");
            fields.Add("parameterCount");
            fields.Add("parametersTruncated");
        }

        return fields;
    }

    private static List<string> ReadParameterNames(JsonElement args)
    {
        var names = new List<string>();
        foreach (var entry in AgentJson.GetStringArray(args, "parameterNames"))
        {
            var name = entry.Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        return names;
    }

    private static List<FamilySymbol> CollectTypes(
        Document doc,
        List<Autodesk.Revit.DB.Family> families,
        List<FamilySymbol> directTypes)
    {
        var types = new List<FamilySymbol>();
        var seenTypeIds = new HashSet<long>();
        foreach (var directType in directTypes)
        {
            if (seenTypeIds.Add(directType.Id.GetValue()))
                types.Add(directType);
        }

        foreach (var family in families)
        {
            foreach (var typeId in family.GetFamilySymbolIds())
            {
                if (!seenTypeIds.Add(typeId.GetValue()) || doc.GetElement(typeId) is not FamilySymbol symbol)
                    continue;

                types.Add(symbol);
            }
        }

        types.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        var knownFamilyIds = new HashSet<long>(families.Select(family => family.Id.GetValue()));
        foreach (var type in types)
        {
            if (knownFamilyIds.Add(type.Family.Id.GetValue()))
                families.Add(type.Family);
        }

        return types;
    }

    private static (HashSet<long>? PlacedTypeIds, Dictionary<long, List<FamilyInstance>>? InstancesByType)
        CollectPlacedInstances(
            Document doc,
            HashSet<long> pageTypeIds,
            bool needPlacedFlag,
            bool instanceScope,
            CancellationToken ct)
    {
        if (!needPlacedFlag && !instanceScope)
            return (null, null);

        var placedTypeIds = new HashSet<long>();
        var instancesByType = instanceScope
            ? new Dictionary<long, List<FamilyInstance>>()
            : null;
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilyInstance instance || instance.Symbol is null)
                continue;

            var symbolId = instance.Symbol.Id.GetValue();
            placedTypeIds.Add(symbolId);
            if (instancesByType is null || !pageTypeIds.Contains(symbolId))
                continue;

            if (!instancesByType.TryGetValue(symbolId, out var placed))
                instancesByType.Add(symbolId, placed = new List<FamilyInstance>());
            placed.Add(instance);
        }

        return (placedTypeIds, instancesByType);
    }

    private static bool IsTrue(JsonElement args, string property) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static void AppendField(List<string> fields, string field)
    {
        if (!fields.Contains(field, StringComparer.OrdinalIgnoreCase))
            fields.Add(field);
    }
}
