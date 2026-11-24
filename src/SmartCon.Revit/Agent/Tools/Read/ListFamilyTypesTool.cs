using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the types (FamilySymbol) of one family or of a direct type selection,
/// including types that are not placed in the model.
/// </summary>
public sealed partial class ListFamilyTypesTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;
    private const int DefaultParameterLimit = 20;
    private const int MaxParameterLimit = 100;
    private const int MaxParameterNames = 10;
    private const int MaxTypeIds = 200;
    private const int MaxInstancesCheckedForVariance = 50;
    private const int MaxFamilyNames = 50;

    private static readonly string[] AvailableFields =
    {
        "typeId", "name", "familyId", "familyName", "isPlaced", "parameters", "parameterCount"
    };

    private static readonly string[] DefaultFields = { "typeId", "name", "familyId", "familyName" };

    public ListFamilyTypesTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_family_types",
        Description =
            "Lists the types (типоразмеры) of a family, including types that are not placed " +
            "in the model. familyId, familyIds[], familyUniqueId or familyName select the " +
            "family; familyNames[] selects several families by name at once (up to 50, every " +
            "name must resolve - the same matching as familyName); typeIds[] select types " +
            "directly - family types and system family types (WallType, PipeType, …) both " +
            "resolve; system types are returned in systemTypes[] with familyKind=system " +
            "(familyName is the honest system family name, e.g. Basic Wall). " +
            "Records are compact by default " +
            "(typeId, name, familyId, familyName): fields adds isPlaced, parameters and " +
            "parameterCount, compact=false returns the full record, parameterNames adds the " +
            "values of the listed parameters per type.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""},""familyId"":{""type"":""integer""},""familyIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":50,""description"":""Batch: several families in one call (#471)""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""familyNames"":{""type"":""array"",""items"":{""type"":""string""},""minItems"":1,""maxItems"":50,""description"":""Batch: several families by NAME in one call. Every name must resolve like familyName (case-insensitive exact name; an ambiguous name fails). Pass either familyIds or familyNames, not both""},""typeIds"":{""type"":""array"",""items"":{""type"":""integer""},""maxItems"":200,""description"":""Direct type selection, in addition to the family selectors. Every id must resolve to an element type: family types AND system family types (WallType, PipeType, …) both work; system types come back in systemTypes[] with familyKind=system.""},""includeParameters"":{""type"":""boolean"",""default"":true},""parameterLimit"":{""type"":""integer"",""default"":20,""maximum"":100},""includePlaced"":{""type"":""boolean"",""default"":true},""fields"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Record fields to return in compact mode. Allowed: typeId, name, familyId, familyName, isPlaced, parameters, parameterCount. Default: typeId, name, familyId, familyName. Unknown names return invalid_arguments.""},""compact"":{""type"":""boolean"",""default"":true,""description"":""true: each record carries only the fields listed in fields (plus values when parameterNames is passed). false: the full record, including parameters, parameterCount and parametersTruncated.""},""parameterNames"":{""type"":""array"",""items"":{""type"":""string""},""maxItems"":10,""description"":""Adds values {parameter: display string} to each type for the listed parameters. A parameter the type does not have is omitted from that type's values; a parameter it has without a value is null. missingParameters lists names no returned type has.""},""valueScope"":{""type"":""string"",""enum"":[""type"",""instance""],""default"":""type"",""description"":""type: the type's own parameters. instance: parameters read from placed instances; a type without placed instances gets no values, valuesVary=true when other placed instances differ.""},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var parameterNames = ReadParameterNames(args);
        if (parameterNames.Count > MaxParameterNames)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "parameterNames accepts at most " + MaxParameterNames + " names; got " +
                parameterNames.Count + ".");
        }

        var requestedTypeIds = AgentJson.GetLongArray(args, "typeIds");
        if (requestedTypeIds.Count > MaxTypeIds)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "typeIds accepts at most " + MaxTypeIds + " ids; got " + requestedTypeIds.Count + ".");
        }

        var directFailure = ResolveDirectTypes(
            doc, requestedTypeIds, out var directTypes, out var directSystemTypes);
        if (directFailure is not null)
            return directFailure;

        var familiesFailure = ResolveFamilies(
            doc, args, directTypes.Count > 0 || directSystemTypes.Count > 0,
            out var families, out var isFamilyBatch);
        if (familiesFailure is not null)
            return familiesFailure;

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
        var compact = AgentJson.GetBool(args, "compact", true);
        var instanceScope = string.Equals(
            AgentJson.GetString(args, "valueScope"), "instance", StringComparison.OrdinalIgnoreCase);

        var fieldsFailure = ResolveFields(
            args, includePlaced, includeParameters, out var selection, out var fields);
        if (fieldsFailure is not null)
            return fieldsFailure;

        var types = CollectTypes(doc, families, directTypes);
        var page = AgentPaging.Slice(types, offset, limit);
        var pageTypeIds = new HashSet<long>(page.Select(type => type.Id.GetValue()));
        var reportFields = compact
            ? fields
            : FullRecordFields(includePlaced, includeParameters);
        var needTypeParameters = parameterNames.Count > 0 && !instanceScope
                                 || reportFields.Contains("parameters")
                                 || reportFields.Contains("parameterCount");
        var (placedTypeIds, instancesByType) = CollectPlacedInstances(
            doc, pageTypeIds, reportFields.Contains("isPlaced"), instanceScope, ct);

        var foundParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parametersTruncated = false;
        var reports = new List<Dictionary<string, object?>>(page.Count);
        foreach (var symbol in page)
        {
            ct.ThrowIfCancellationRequested();
            var typeParameters = needTypeParameters
                ? ReadTypeParameters(symbol, parameterLimit, parameterNames)
                : null;
            var report = compact
                ? BuildCompactReport(symbol, typeParameters, fields, placedTypeIds, ref parametersTruncated)
                : BuildFullReport(symbol, typeParameters, placedTypeIds, includeParameters);

            var valuesVary = false;
            if (parameterNames.Count > 0
                && ReadValues(
                    doc, parameterNames, typeParameters, instancesByType,
                    symbol.Id.GetValue(), ref valuesVary) is { } values)
            {
                report["values"] = values;
                foreach (var name in values.Keys)
                    foundParameters.Add(name);
                if (valuesVary)
                    report["valuesVary"] = true;
            }

            reports.Add(report);
        }

        var pageEnvelope = AgentListPayload.BuildPage(types.Count, offset, reports.Count);
        var singleFamily = families.Count == 1 ? families[0] : null;
        var payload = new Dictionary<string, object?>
        {
            ["familyId"] = singleFamily?.Id.GetValue(),
            ["familyName"] = singleFamily?.Name,
            ["familyCount"] = families.Count,
            ["category"] = singleFamily?.FamilyCategory?.Name,
            ["types"] = reports
        };
        pageEnvelope.AddTo(payload);
        AgentListPayload.AddFieldInfo(payload, reportFields, AvailableFields);

        var systemRows = directSystemTypes.Count > 0
            ? BuildSystemTypeRows(
                doc, directSystemTypes, parameterNames, instanceScope,
                CollectUsedTypeIds(doc, reportFields.Contains("isPlaced"), ct),
                ct)
            : null;
        if (systemRows is not null)
        {
            payload["systemTypes"] = systemRows;
            payload["systemTypeCount"] = systemRows.Count;
            foreach (var row in systemRows)
            {
                if (row.TryGetValue("values", out var values)
                    && values is Dictionary<string, object?> valueMap)
                {
                    foreach (var name in valueMap.Keys)
                        foundParameters.Add(name);
                }
            }
        }

        var missingParameters = new List<string>();
        if (parameterNames.Count > 0
            && (reports.Count > 0 || (systemRows?.Count ?? 0) > 0))
        {
            payload["valueScope"] = instanceScope ? "instance" : "type";
            foreach (var name in parameterNames)
            {
                if (!foundParameters.Contains(name))
                    missingParameters.Add(name);
            }

            if (missingParameters.Count > 0)
                payload["missingParameters"] = missingParameters;
        }

        if (parametersTruncated)
            payload["parametersTruncated"] = true;

        var subject = families.Count == 1
            ? "Family '" + families[0].Name + "'"
            : families.Count + " families";
        var messageText = types.Count == 0 && (systemRows?.Count ?? 0) == 0
            ? subject + " has no types (empty family)."
            : subject + ": " + reports.Count + " of " + types.Count +
              " type(s)" + (pageEnvelope.Truncated ? ", next offset " + pageEnvelope.NextOffset : string.Empty) +
              ((systemRows?.Count ?? 0) > 0
                  ? "; " + systemRows!.Count +
                    " system family type(s) (no loadable family — see systemTypes)"
                  : string.Empty) + ".";
        // Live case 5 round 2: reading families one-per-call burned the round
        // budget; nudge the batch parameter for the rest of the list.
        if (families.Count == 1 && !isFamilyBatch && directTypes.Count == 0)
            messageText += " Reading several families? Pass familyIds:[...] or familyNames:[...] — up to 50 in one call.";
        if (pageEnvelope.Truncated)
            messageText += " Narrow with typeIds or fewer fields.";
        if (selection.IsDefault && compact && reports.Count >= AgentListPayload.CompactHintThreshold)
        {
            messageText += AgentListPayload.CompactFieldsHint(
                fields,
                parameterNames.Count == 0 ? " Pass parameterNames=[...] to add parameter values." : string.Empty);
        }

        if (missingParameters.Count > 0)
        {
            messageText += " No returned type has parameter(s): " +
                           string.Join(", ", missingParameters) + ".";
        }

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
