using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Live scan (no persistent index) over loaded family types: finds families whose
/// types expose a parameter with the requested name (and optionally value).
/// Instance-scope parameters live on placed instances, not on the family symbol,
/// so they need the separate <c>includeInstanceParameters</c> pass.
/// </summary>
public sealed class FindFamiliesWithParameterTool : AgentReadToolBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    /// <summary>
    /// Inline cap per family: a family with hundreds of matching types must not
    /// push the whole answer over the context budget (the trimmed envelope then
    /// looked like "nothing found": families=[] with familiesTotal>0).
    /// </summary>
    private const int DefaultMaxMatchedTypes = 10;

    private const int MaxMatchedTypes = 50;

    private const int MaxSampleInstances = 5;

    public FindFamiliesWithParameterTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "find_families_with_parameter",
        Description =
            "Finds loaded families whose family types expose a parameter with the given name " +
            "(exact or contains match) and optional value. Scans the live project on every " +
            "call — no index; unplaced types are included. By default only type parameters are " +
            "searched. With includeInstanceParameters=true placed family instances are also " +
            "scanned, so instance-scope shared parameters (ADSK_Наименование and similar) are " +
            "found; families with no placed instances are flagged instanceParametersUnknown=true " +
            "(their instance parameters can only be checked by opening the family document via " +
            "get_family_parameters). matchedTypes and matchedInstanceParameters are " +
            "capped per family by maxMatchedTypes (default 10, max 50); the totals and *Truncated " +
            "flags show how many matches exist in full. compact=true returns *Names instead of " +
            "full rows.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""parameterName"":{""type"":""string""},""match"":{""type"":""string"",""enum"":[""exact"",""contains""],""default"":""exact""},""valueContains"":{""type"":""string""},""category"":{""type"":""string""},""includeInstanceParameters"":{""type"":""boolean"",""default"":false},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""maxMatchedTypes"":{""type"":""integer"",""default"":10,""minimum"":1,""maximum"":50},""compact"":{""type"":""boolean"",""default"":false},""offset"":{""type"":""integer"",""default"":0}},""required"":[""parameterName""],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var parameterName = AgentJson.GetString(args, "parameterName");
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "parameterName is required: pass the parameter name to search for.");
        }

        var matchText = AgentJson.GetString(args, "match");
        var contains = string.Equals(matchText, "contains", StringComparison.OrdinalIgnoreCase);
        var valueContains = AgentJson.GetString(args, "valueContains");
        var categoryFilter = AgentJson.GetString(args, "category");
        var includeInstanceParameters = AgentJson.GetBool(args, "includeInstanceParameters", false);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);
        var maxMatchedTypes = Math.Max(
            1,
            Math.Min(
                AgentJson.GetInt(args, "maxMatchedTypes", DefaultMaxMatchedTypes),
                MaxMatchedTypes));
        var compact = AgentJson.GetBool(args, "compact", false);

        HashSet<long>? categoryIds = null;
        if (!string.IsNullOrWhiteSpace(categoryFilter))
        {
            var unknown = new List<string>();
            var resolved = AgentCategoryResolver.Resolve(
                doc, new[] { categoryFilter! }, unknown);
            if (resolved.Count == 0)
            {
                return ToolResult.Fail(
                    "unknown_category",
                    "Category '" + categoryFilter + "' was not found" +
                    AgentCategoryResolver.SuggestSuffix(doc, categoryFilter!) + ". " +
                    "[Action: pass a category name from list_categories or an OST_* built-in name]");
            }

            categoryIds = new HashSet<long>(resolved.Select(id => id.GetValue()));
        }

        var families = new Dictionary<long, FamilyMatch>();
        var familiesSeen = new HashSet<long>();
        var typesScanned = 0;
        var instancesScanned = 0;

        var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
        foreach (var element in collector)
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilySymbol symbol)
                continue;

            typesScanned++;
            var family = symbol.Family;
            if (family is null)
                continue;

            familiesSeen.Add(family.Id.GetValue());
            if (!MatchesCategory(family, categoryIds))
                continue;

            FamilyMatch? familyMatch = null;
            var matchTotal = 0;
            var seenParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Parameter parameter in symbol.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                if (!MatchesName(name!, parameterName!, contains))
                    continue;

                var value = SafeValueString(parameter);
                if (!string.IsNullOrWhiteSpace(valueContains)
                    && !AgentFamilySpecs.ContainsNormalized(value, valueContains!))
                {
                    continue;
                }

                if (!seenParameterNames.Add(name!))
                    continue;

                matchTotal++;
                familyMatch ??= GetOrCreateFamily(families, family);

                // The cap belongs to the family, not to one type: a door family with
                // 40 matching types used to return 40 rows (external run 6, P2).
                if (familyMatch.TypeMatches.Count >= maxMatchedTypes)
                    continue;

                familyMatch.TypeMatches.Add(new Dictionary<string, object?>
                {
                    ["scope"] = "type",
                    ["typeId"] = symbol.Id.GetValue(),
                    ["typeName"] = symbol.Name,
                    ["parameterName"] = name,
                    ["value"] = value
                });
            }

            if (matchTotal == 0 || familyMatch is null)
                continue;

            familyMatch.MatchedTypesTotal += matchTotal;
        }

        if (includeInstanceParameters)
        {
            ScanPlacedInstances(
                doc,
                categoryIds,
                parameterName!,
                contains,
                valueContains,
                maxMatchedTypes,
                families,
                familiesSeen,
                ref instancesScanned,
                ct);
        }

        var ordered = new List<FamilyMatch>(families.Values);
        ordered.Sort((left, right) =>
            string.Compare(left.FamilyName, right.FamilyName, StringComparison.OrdinalIgnoreCase));

        var page = new List<Dictionary<string, object?>>();
        for (var index = offset; index < ordered.Count && page.Count < limit; index++)
        {
            var item = ordered[index];
            page.Add(new Dictionary<string, object?>
            {
                ["familyId"] = item.FamilyId,
                ["familyName"] = item.FamilyName,
                ["familyUniqueId"] = item.FamilyUniqueId,
                ["category"] = item.Category,
                ["matchedTypeCount"] = item.MatchedTypesTotal,
                ["matchedTypesReturned"] = item.TypeMatches.Count,
                ["matchedTypesTruncated"] = item.MatchedTypesTotal > item.TypeMatches.Count,
                ["matchedTypeNames"] = compact
                    ? item.TypeMatches.Select(match => match["typeName"]).ToList()
                    : null,
                ["matchedTypes"] = compact ? null : item.TypeMatches,
                ["placedInstanceCount"] = item.PlacedInstanceCount,
                ["instanceParametersUnknown"] =
                    includeInstanceParameters && item.PlacedInstanceCount == 0,
                ["matchedInstanceParameterCount"] = item.MatchedInstanceParametersTotal,
                ["matchedInstanceParametersReturned"] = item.InstanceMatches.Count,
                ["matchedInstanceParametersTruncated"] =
                    item.MatchedInstanceParametersTotal > item.InstanceMatches.Count,
                ["matchedInstanceParameterNames"] = compact
                    ? item.InstanceMatches.Select(match => match["parameterName"]).ToList()
                    : null,
                ["matchedInstanceParameters"] = compact ? null : item.InstanceMatches
            });
        }

        var nextOffset = offset + page.Count;
        var typeMatches = ordered.Sum(item => item.MatchedTypesTotal);
        var instanceMatches = ordered.Sum(item => item.MatchedInstanceParametersTotal);
        var payload = new Dictionary<string, object?>
        {
            ["parameterName"] = parameterName,
            ["match"] = contains ? "contains" : "exact",
            ["includeInstanceParameters"] = includeInstanceParameters,
            ["compact"] = compact,
            ["families"] = page,
            ["total"] = ordered.Count,
            ["returned"] = page.Count,
            ["offset"] = offset,
            ["nextOffset"] = nextOffset < ordered.Count ? nextOffset : (int?)null,
            ["truncated"] = nextOffset < ordered.Count,
            ["familiesScanned"] = familiesSeen.Count,
            ["typesScanned"] = typesScanned,
            ["instancesScanned"] = instancesScanned,
            ["typeMatchesTotal"] = typeMatches,
            ["instanceParametersTotal"] = instanceMatches,
            ["scopeNote"] =
                "Type parameters are read from family symbols. With includeInstanceParameters=true " +
                "instance-scope parameters are read from placed instances; a family with no " +
                "placed instances can only be checked by opening its document with " +
                "get_family_parameters."
        };

        var messageText = ordered.Count == 0
            ? "No loaded family exposes a parameter matching '" + parameterName + "'. " +
              "Scanned " + typesScanned + " type(s) in " + familiesSeen.Count +
              " family(ies)" +
              (includeInstanceParameters
                  ? " and " + instancesScanned + " placed instance(s)."
                  : "; pass includeInstanceParameters=true to also scan placed instances.") +
              " Use list_families / get_family_parameters to inspect a family."
            : "Found " + ordered.Count + " family(ies) with parameter '" + parameterName +
              "' (" + typeMatches + " type match(es)" +
              (includeInstanceParameters ? ", " + instanceMatches + " instance parameter(s)" : "") +
              "); returned " + page.Count +
              (nextOffset < ordered.Count ? ", next offset " + nextOffset : string.Empty) + ".";

        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
    }

    private static void ScanPlacedInstances(
        Document doc,
        HashSet<long>? categoryIds,
        string parameterName,
        bool contains,
        string? valueContains,
        int maxMatchedTypes,
        Dictionary<long, FamilyMatch> families,
        HashSet<long> familiesSeen,
        ref int instancesScanned,
        CancellationToken ct)
    {
        var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance));
        foreach (var element in collector)
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilyInstance instance)
                continue;

            instancesScanned++;
            var family = instance.Symbol?.Family;
            if (family is null)
                continue;

            familiesSeen.Add(family.Id.GetValue());
            if (!MatchesCategory(family, categoryIds))
                continue;

            var familyMatch = GetOrCreateFamily(families, family);
            familyMatch.PlacedInstanceCount++;

            var seenParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Parameter parameter in instance.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                if (!MatchesName(name!, parameterName, contains))
                    continue;

                var value = SafeValueString(parameter);
                if (!string.IsNullOrWhiteSpace(valueContains)
                    && !AgentFamilySpecs.ContainsNormalized(value, valueContains!))
                {
                    continue;
                }

                if (!seenParameterNames.Add(name!))
                    continue;

                if (!familyMatch.InstanceParameters.TryGetValue(name!, out var match))
                {
                    match = new InstanceParameterMatch
                    {
                        ParameterName = name!,
                        FirstValue = value
                    };
                    familyMatch.InstanceParameters.Add(name!, match);
                    familyMatch.MatchedInstanceParametersTotal++;

                    if (familyMatch.InstanceMatches.Count < maxMatchedTypes)
                    {
                        familyMatch.InstanceMatches.Add(match.ToRow());
                    }
                }

                match.AddInstance(instance.Id.GetValue(), value);
                if (match.Row is not null)
                    match.SyncRow();
            }
        }
    }

    private static FamilyMatch GetOrCreateFamily(
        Dictionary<long, FamilyMatch> families,
        Autodesk.Revit.DB.Family family)
    {
        var id = family.Id.GetValue();
        if (families.TryGetValue(id, out var existing))
            return existing;

        var created = new FamilyMatch
        {
            FamilyId = id,
            FamilyUniqueId = family.UniqueId,
            FamilyName = family.Name,
            Category = family.FamilyCategory?.Name
        };
        families.Add(id, created);
        return created;
    }

    private static bool MatchesCategory(
        Autodesk.Revit.DB.Family family,
        HashSet<long>? categoryIds) =>
        categoryIds is null
        || (family.FamilyCategory is not null
            && categoryIds.Contains(family.FamilyCategory.Id.GetValue()));

    private static bool MatchesName(string name, string parameterName, bool contains) =>
        contains
            ? AgentFamilySpecs.ContainsNormalized(name, parameterName)
            : string.Equals(name, parameterName.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? SafeValueString(Parameter parameter)
    {
        try
        {
            return parameter.AsValueString() ?? parameter.AsString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class FamilyMatch
    {
        public long FamilyId { get; init; }

        public required string FamilyUniqueId { get; init; }

        public required string FamilyName { get; init; }

        public string? Category { get; init; }

        public int MatchedTypesTotal { get; set; }

        public int MatchedInstanceParametersTotal { get; set; }

        public int PlacedInstanceCount { get; set; }

        public List<Dictionary<string, object?>> TypeMatches { get; } = new();

        public List<Dictionary<string, object?>> InstanceMatches { get; } = new();

        public Dictionary<string, InstanceParameterMatch> InstanceParameters { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class InstanceParameterMatch
    {
        public required string ParameterName { get; init; }

        public string? FirstValue { get; init; }

        public int InstanceCount { get; private set; }

        public List<long> SampleInstanceIds { get; } = new();

        public List<string> SampleValues { get; } = new();

        public Dictionary<string, object?>? Row { get; private set; }

        public void AddInstance(long instanceId, string? value)
        {
            InstanceCount++;
            if (SampleInstanceIds.Count < MaxSampleInstances
                && !SampleInstanceIds.Contains(instanceId))
            {
                SampleInstanceIds.Add(instanceId);
            }

            if (SampleValues.Count < MaxSampleInstances
                && !string.IsNullOrWhiteSpace(value)
                && !SampleValues.Contains(value!))
            {
                SampleValues.Add(value!);
            }
        }

        public Dictionary<string, object?> ToRow()
        {
            Row = new Dictionary<string, object?>
            {
                ["scope"] = "instance",
                ["parameterName"] = ParameterName,
                ["value"] = FirstValue,
                ["instanceCount"] = 0,
                ["sampleInstanceIds"] = SampleInstanceIds,
                ["sampleValues"] = SampleValues
            };
            return Row;
        }

        public void SyncRow()
        {
            if (Row is null)
                return;

            Row["instanceCount"] = InstanceCount;
            Row["value"] = FirstValue ?? (SampleValues.Count > 0 ? SampleValues[0] : null);
            Row["sampleInstanceIds"] = SampleInstanceIds;
            Row["sampleValues"] = SampleValues;
        }
    }
}