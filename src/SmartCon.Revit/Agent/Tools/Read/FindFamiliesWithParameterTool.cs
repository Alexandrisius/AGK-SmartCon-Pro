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

    public FindFamiliesWithParameterTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "find_families_with_parameter",
        Description =
            "Finds loaded families whose family types expose a parameter with the given name " +
            "(exact or contains match) and optional value. Scans the live project on every " +
            "call — no index; unplaced types are included. Searches parameters visible on " +
            "family types; instance values of placed instances are not scanned. " +
            "matchedTypes is capped per family by maxMatchedTypes (default 10, max 50); the " +
            "totals and matchedTypesTruncated show how many matches exist in full. " +
            "compact=true returns matchedTypeNames instead of full matchedTypes rows.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""parameterName"":{""type"":""string""},""match"":{""type"":""string"",""enum"":[""exact"",""contains""],""default"":""exact""},""valueContains"":{""type"":""string""},""category"":{""type"":""string""},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""maxMatchedTypes"":{""type"":""integer"",""default"":10,""minimum"":1,""maximum"":50},""compact"":{""type"":""boolean"",""default"":false},""offset"":{""type"":""integer"",""default"":0}},""required"":[""parameterName""],""additionalProperties"":false}"
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
                    "Category '" + categoryFilter + "' was not found. " +
                    "[Action: pass a category name from list_categories or an OST_* built-in name]");
            }

            categoryIds = new HashSet<long>(resolved.Select(id => id.GetValue()));
        }

        var families = new Dictionary<long, FamilyMatch>();
        var familiesSeen = new HashSet<long>();
        var typesScanned = 0;

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
            if (categoryIds is not null
                && (family.FamilyCategory is null
                    || !categoryIds.Contains(family.FamilyCategory.Id.GetValue())))
            {
                continue;
            }

            FamilyMatch? familyMatch = null;
            var matchTotal = 0;
            var seenParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Parameter parameter in symbol.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                var nameMatches = contains
                    ? AgentFamilySpecs.ContainsNormalized(name, parameterName!)
                    : string.Equals(name, parameterName!.Trim(), StringComparison.OrdinalIgnoreCase);
                if (!nameMatches)
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
                if (familyMatch is null)
                {
                    if (!families.TryGetValue(family.Id.GetValue(), out familyMatch))
                    {
                        familyMatch = new FamilyMatch
                        {
                            FamilyId = family.Id.GetValue(),
                            FamilyUniqueId = family.UniqueId,
                            FamilyName = family.Name,
                            Category = family.FamilyCategory?.Name
                        };
                        families.Add(family.Id.GetValue(), familyMatch);
                    }
                }

                // The cap belongs to the family, not to one type: a door family with
                // 40 matching types used to return 40 rows (external run 6, P2).
                if (familyMatch.Matches.Count >= maxMatchedTypes)
                    continue;

                familyMatch.Matches.Add(new Dictionary<string, object?>
                {
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
                ["matchedTypesReturned"] = item.Matches.Count,
                ["matchedTypesTruncated"] = item.MatchedTypesTotal > item.Matches.Count,
                ["matchedTypeNames"] = compact
                    ? item.Matches.Select(match => match["typeName"]).ToList()
                    : null,
                ["matchedTypes"] = compact ? null : item.Matches
            });
        }

        var nextOffset = offset + page.Count;
        var payload = new Dictionary<string, object?>
        {
            ["parameterName"] = parameterName,
            ["match"] = contains ? "contains" : "exact",
            ["compact"] = compact,
            ["families"] = page,
            ["total"] = ordered.Count,
            ["returned"] = page.Count,
            ["offset"] = offset,
            ["nextOffset"] = nextOffset < ordered.Count ? nextOffset : (int?)null,
            ["truncated"] = nextOffset < ordered.Count,
            ["familiesScanned"] = familiesSeen.Count,
            ["typesScanned"] = typesScanned,
            ["scopeNote"] =
                "Searches parameters exposed on family types (type and shared parameters). " +
                "Instance values live on placed instances and are not scanned."
        };

        var messageText = ordered.Count == 0
            ? "No loaded family type exposes a parameter matching '" + parameterName + "'. " +
              "Scanned " + typesScanned + " type(s) in " + familiesSeen.Count +
              " family(ies); use list_families / get_family_parameters to inspect a family."
            : "Found " + ordered.Count + " family(ies) with parameter '" + parameterName +
              "'; returned " + page.Count +
              (nextOffset < ordered.Count ? ", next offset " + nextOffset : string.Empty) + ".";

        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
    }

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

        public List<Dictionary<string, object?>> Matches { get; } = new();
    }
}