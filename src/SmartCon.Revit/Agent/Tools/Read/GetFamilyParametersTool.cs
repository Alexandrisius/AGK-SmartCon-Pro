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
/// canSetValue/canSetFormula) and values per family type.
/// </summary>
public sealed class GetFamilyParametersTool : AgentReadToolBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;
    private const int DefaultMaxValues = 20;
    private const int MaxValues = 100;

    public GetFamilyParametersTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_family_parameters",
        Description =
            "Lists family parameters with their scope (instance/type), shared GUID, isBuiltIn " +
            "(built-in parameters cannot be removed), storage type, formula, lookup tables, " +
            "canSetValue/canSetFormula and the value of each family type. Opens the family " +
            "document in the background; the Family Editor is never shown.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""nameContains"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""all"",""instance"",""type""],""default"":""all""},""includeValues"":{""type"":""boolean"",""default"":true},""maxValuesPerParameter"":{""type"":""integer"",""default"":20,""maximum"":100},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
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

        var scopeText = AgentJson.GetString(args, "scope");
        bool? wantInstance = null;
        if (!string.IsNullOrWhiteSpace(scopeText)
            && !string.Equals(scopeText, "all", StringComparison.OrdinalIgnoreCase))
        {
            wantInstance = AgentFamilyReader.ParseScope(scopeText, out var scopeMessage);
            if (wantInstance is null)
            {
                return ToolResult.Fail(
                    "invalid_arguments",
                    scopeMessage ?? "scope must be all, instance or type.");
            }
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
            return ToolResult.Fail(
                openErrorCode ?? "family_open_failed",
                openMessage ?? "The family document could not be opened.");
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
                ["parameterCount"] = manager.GetParameters().Count,
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

            return ToolResult.Ok(
                AgentJson.Serialize(payload),
                "Family '" + family.Name + "': " + reports.Count + " of " + filtered.Count +
                " parameter(s), " + types.Count + " type(s)" +
                (nextOffset < filtered.Count ? ", next offset " + nextOffset : string.Empty) +
                (types.Count == 0 ? ". The family has no types (empty family)." : "."));
        }
        finally
        {
            CloseQuietly(familyDoc);
        }
    }

    private static void CloseQuietly(Document? familyDoc)
    {
        if (familyDoc is null)
            return;

        try
        {
            familyDoc.Close(false);
        }
        catch (Exception ex)
        {
            SmartCon.Core.Logging.SmartConLogger.Warn(
                "Agent family document could not be closed after reading: " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: close the family document in Revit so it is not left open]");
        }
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