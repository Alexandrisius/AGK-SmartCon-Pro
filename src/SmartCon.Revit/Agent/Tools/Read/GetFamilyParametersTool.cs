using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Reads the parameters of a family document with type scope: definition
/// metadata (instance/type, shared GUID, storage, formula, lookup tables,
/// canSetValue/canSetFormula) and values per family type. The batch form
/// (<c>familyIds</c>, up to 20) reads several families in one call - the
/// benchmark kept losing rounds to one-by-one calls over family lists.
/// </summary>
public sealed class GetFamilyParametersTool : AgentReadToolBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;
    private const int DefaultMaxValues = 20;
    private const int MaxValues = 100;
    private const int LargeResultHintChars = 20000;

    public GetFamilyParametersTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_family_parameters",
        Description =
            "Lists family parameters with their scope (instance/type), id (FamilyParameter.Id), " +
            "shared GUID, isBuiltIn (built-in parameters cannot be removed), storage type, " +
            "formula, lookup tables, canSetValue/canSetFormula and the value of each family type. " +
            "parametersTotalInFamily is the count BEFORE nameContains/scope filters; total is " +
            "the paging total of the filtered set. parameters[] is flat: address a row with " +
            "parameters.byId[id] or parameters.byName[name], a type value with " +
            "valuesByType.byName[typeName]. " +
            "familyIds (array, up to 20) reads several families in ONE call and returns " +
            "families[] with the same per-family payload (nameContains/scope/limits apply " +
            "to every family). " +
            "Opens the family document in the background; the Family Editor is never shown.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""familyId"":{""type"":""integer""},""familyIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":20,""description"":""Batch: read several families in one call; each entry gets the same payload as the single-family form""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""nameContains"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""all"",""instance"",""type""],""default"":""all""},""includeValues"":{""type"":""boolean"",""default"":true},""maxValuesPerParameter"":{""type"":""integer"",""default"":20,""maximum"":100},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var batchIds = TryGetIntArray(args, "familyIds");
        if (batchIds is { Count: > 0 })
            return ExecuteBatch(doc, args, batchIds, ct);

        var family = AgentFamilyReader.ResolveFamily(
            doc,
            TryGetLong(args, "familyId"),
            AgentJson.GetString(args, "familyName"),
            AgentJson.GetString(args, "familyUniqueId"),
            out var errorCode,
            out var message);
        if (family is null)
            return ToolResult.Fail(errorCode ?? "family_not_found", message ?? "Family not found.");

        var (payload, familyMessage, failure) = ReadFamily(doc, args, family, ct);
        if (payload is null)
            return ToolResult.Fail(
                failure ?? "family_open_failed", familyMessage);

        var json = AgentJson.Serialize(payload);

        // One gentle nudge when the single-family result is large: the model
        // that keeps calling this per family should switch to the batch form.
        if (json.GetRawText().Length > LargeResultHintChars
            && familyMessage.IndexOf("familyIds", StringComparison.Ordinal) < 0)
        {
            familyMessage += " NOTE: reading several families? Pass familyIds (up to 20) " +
                             "to read them all in one call.";
        }

        return ToolResult.Ok(json, familyMessage);
    }

    private ToolResult ExecuteBatch(
        Document doc,
        JsonElement args,
        IReadOnlyList<long> familyIds,
        CancellationToken ct)
    {
        var families = new List<object?>(familyIds.Count);
        var okCount = 0;
        foreach (var id in familyIds)
        {
            ct.ThrowIfCancellationRequested();

            var family = AgentFamilyReader.ResolveFamily(
                doc, id, null, null, out var errorCode, out var message);
            if (family is null)
            {
                families.Add(new Dictionary<string, object?>
                {
                    ["familyId"] = id,
                    ["ok"] = false,
                    ["error"] = errorCode ?? "family_not_found",
                    ["message"] = message ?? "Family not found."
                });
                continue;
            }

            var (payload, familyMessage, failure) = ReadFamily(doc, args, family, ct);
            if (payload is null)
            {
                families.Add(new Dictionary<string, object?>
                {
                    ["familyId"] = id,
                    ["ok"] = false,
                    ["error"] = failure ?? "family_open_failed",
                    ["message"] = familyMessage
                });
                continue;
            }

            payload["ok"] = true;
            families.Add(payload);
            okCount++;
        }

        var failedCount = familyIds.Count - okCount;
        var batchPayload = new Dictionary<string, object?>
        {
            ["families"] = families,
            ["familyCount"] = familyIds.Count,
            ["okCount"] = okCount,
            ["failedCount"] = failedCount
        };

        return ToolResult.Ok(
            AgentJson.Serialize(batchPayload),
            "Read " + okCount + " of " + familyIds.Count + " family(ies)" +
            (failedCount > 0 ? ", " + failedCount + " not found" : string.Empty) + ".");
    }

    private (Dictionary<string, object?>? Payload, string Message, string? ErrorCode) ReadFamily(
        Document doc,
        JsonElement args,
        Autodesk.Revit.DB.Family family,
        CancellationToken ct)
    {
        var scopeText = AgentJson.GetString(args, "scope");
        bool? wantInstance = null;
        if (!string.IsNullOrWhiteSpace(scopeText)
            && !string.Equals(scopeText, "all", StringComparison.OrdinalIgnoreCase))
        {
            wantInstance = AgentFamilyReader.ParseScope(scopeText, out var scopeMessage);
            if (wantInstance is null)
                return (null, scopeMessage ?? "scope must be all, instance or type.",
                    "invalid_arguments");
        }

        var nameContains = AgentJson.GetString(args, "nameContains");
        var includeValues = AgentJson.GetBool(args, "includeValues", true);
        var maxValues = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "maxValuesPerParameter", DefaultMaxValues),
            DefaultMaxValues,
            MaxValues);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);

        if (!AgentFamilyReader.TryOpenFamilyDocument(
                doc,
                family,
                out var familyDoc,
                out var openErrorCode,
                out var openMessage))
        {
            return (null,
                openMessage ?? "The family document could not be opened.",
                openErrorCode ?? "family_open_failed");
        }

        try
        {
            var manager = familyDoc!.FamilyManager;
            var types = AgentFamilyReader.GetTypes(manager);

            var filtered = new List<FamilyParameter>();
            foreach (var parameter in manager.GetParameters())
            {
                ct.ThrowIfCancellationRequested();

                if (wantInstance.HasValue && parameter.IsInstance != wantInstance.Value)
                    continue;

                if (!string.IsNullOrWhiteSpace(nameContains)
                    && !AgentFamilySpecs.ContainsNormalized(
                        parameter.Definition.Name,
                        nameContains!))
                {
                    continue;
                }

                filtered.Add(parameter);
            }

            var page = AgentPaging.Slice(filtered, offset, limit);
            var reports = new List<FamilyParameterReport>(page.Count);
            foreach (var parameter in page)
            {
                ct.ThrowIfCancellationRequested();
                reports.Add(AgentFamilyReader.BuildParameterReport(
                    parameter,
                    types,
                    includeValues,
                    maxValues,
                    familyDoc));
            }

            var nextOffset = offset + reports.Count;
            var typeNames = new List<string>();
            for (var index = 0; index < types.Count && index < 100; index++)
                typeNames.Add(types[index].Name);

            var payload = new Dictionary<string, object?>
            {
                ["familyId"] = family.Id.GetValue(),
                ["familyName"] = family.Name,
                ["category"] = family.FamilyCategory?.Name,
                // Total in the family BEFORE nameContains/scope filters: "total" below
                // is the paging total of the filtered set and used to read as "the
                // family only has this many parameters" (external harness feedback).
                ["parametersTotalInFamily"] = manager.GetParameters().Count,
                ["typeCount"] = types.Count,
                ["types"] = typeNames,
                ["typesTruncated"] = types.Count > typeNames.Count,
                ["currentType"] = manager.CurrentType?.Name,
                ["parameters"] = reports,
                ["total"] = filtered.Count,
                ["returned"] = reports.Count,
                ["offset"] = offset,
                ["nextOffset"] = nextOffset < filtered.Count ? nextOffset : (int?)null,
                ["truncated"] = nextOffset < filtered.Count,
                ["maxValuesPerParameter"] = maxValues
            };

            var message = "Family '" + family.Name + "': " + reports.Count + " of " +
                          filtered.Count + " matched parameter(s) of " +
                          manager.GetParameters().Count + " in the family, " + types.Count +
                          " type(s)" +
                          (nextOffset < filtered.Count ? ", next offset " + nextOffset : string.Empty) +
                          (types.Count == 0 ? ". The family has no types (empty family)." : ".");
            return (payload, message, null);
        }
        finally
        {
            CloseQuietly(familyDoc);
        }
    }

    private static void CloseQuietly(Document? familyDoc)
    {
        // FamilyManager batch pattern (#96 / REVIT-237190): close + COM release +
        // render-thread nudge.
        AgentFamilyDocCloser.Close(familyDoc);
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

    private static IReadOnlyList<long>? TryGetIntArray(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var ids = new List<long>();
        foreach (var item in value.EnumerateArray())
        {
            // Non-integer entries never reach the tool in production (schema
            // validation); a malformed direct call falls back to the single path.
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var id))
                return null;
            ids.Add(id);
        }

        return ids;
    }
}
