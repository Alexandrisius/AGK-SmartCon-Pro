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
        out List<FamilySymbol> types,
        out List<ElementType> systemTypes)
    {
        types = new List<FamilySymbol>();
        systemTypes = new List<ElementType>();
        if (requestedTypeIds.Count == 0)
            return null;

        var missing = new List<long>();
        var notTypes = new List<long>();
        var seen = new HashSet<long>();
        foreach (var id in requestedTypeIds)
        {
            if (!seen.Add(id))
                continue;

            var element = doc.GetElement(ElementIdCompat.Create(id));
            if (element is FamilySymbol symbol)
                types.Add(symbol);
            else if (element is ElementType systemType)
                systemTypes.Add(systemType);
            else if (element is null)
                missing.Add(id);
            else
                notTypes.Add(id);
        }

        if (missing.Count == 0 && notTypes.Count == 0)
            return null;

        // Live case 5 round 11: classify_types returns ids of SYSTEM family types
        // (WallType, PipeType, …) — those are real ElementTypes, so they resolve
        // above and never land in this failure. What fails here is an id that is
        // absent or not a type at all, and the action must say which tool reads
        // the id space the caller was probably in.
        var parts = new List<string>();
        if (missing.Count > 0)
            parts.Add("missing: [" + string.Join(", ", missing) + "]");
        if (notTypes.Count > 0)
            parts.Add("not element types (instances and other elements): [" +
                      string.Join(", ", notTypes) + "]");

        return ToolResult.Fail(
            "type_not_found",
            (types.Count + systemTypes.Count == 0
                ? "None of the typeIds resolved to an element type"
                : "typeIds requires every id to resolve to an element type") +
            "; " + string.Join("; ", parts) + ". " +
            "[Action: list_family_types returns family type ids; system family types " +
            "(walls, pipes, ducts…) are listed by list_system_types]",
            data: AgentJson.Serialize(new { missing, notTypes }));
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

        // Batch by names (N-ds-5): the same contract as the familyIds batch -
        // every selector must resolve - and not combinable with it.
        var familyNames = ReadFamilyNames(args);
        if (isBatch && familyNames.Count > 0)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "Pass either familyIds or familyNames, not both.");
        }

        if (familyNames.Count > MaxFamilyNames)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "familyNames accepts at most " + MaxFamilyNames + " names; got " +
                familyNames.Count + ".");
        }

        if (familyNames.Count > 0)
        {
            isBatch = true;
            return ResolveFamilyNames(doc, familyNames, families);
        }

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

    /// <summary>
    /// Batch selection by family names: each name is resolved exactly like the
    /// single <c>familyName</c> selector (case-insensitive exact name, an
    /// ambiguous name fails). Mirrors the familyIds batch: none resolved is a
    /// typed failure, some unresolved is a success that names the misses.
    /// </summary>
    private static ToolResult? ResolveFamilyNames(
        Document doc,
        IReadOnlyList<string> familyNames,
        List<Autodesk.Revit.DB.Family> families)
    {
        var unresolved = new List<Dictionary<string, object?>>();
        foreach (var name in familyNames)
        {
            var family = AgentFamilyReader.ResolveFamily(
                doc, null, name, null, out var errorCode, out var message);
            if (family is not null)
                families.Add(family);
            else
                unresolved.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["errorCode"] = errorCode,
                    ["reason"] = message
                });
        }

        if (families.Count == 0)
        {
            return ToolResult.Fail(
                "family_not_found",
                "None of the familyNames resolved: [" + Names(unresolved) + "]. " +
                "[Action: check the exact names with list_families, or pass familyIds]",
                data: AgentJson.Serialize(new { unresolved }));
        }

        if (unresolved.Count > 0)
        {
            return ToolResult.Ok(
                AgentJson.Serialize(new { unresolved }),
                "familyNames batch requires every name to resolve; unresolved: [" +
                Names(unresolved) + "]. [Action: fix the names with list_families or pass " +
                "familyIds]");
        }

        return null;
    }

    private static string Names(List<Dictionary<string, object?>> rows) =>
        string.Join(", ", rows.Select(row => row["name"]));

    private static List<string> ReadFamilyNames(JsonElement args)
    {
        var names = new List<string>();
        foreach (var entry in AgentJson.GetStringArray(args, "familyNames"))
        {
            var name = entry.Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        return names;
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
