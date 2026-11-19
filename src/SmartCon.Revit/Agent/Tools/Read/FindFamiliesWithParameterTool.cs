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
            "family types; instance values of placed instances are not scanned.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""parameterName"":{""type"":""string""},""match"":{""type"":""string"",""enum"":[""exact"",""contains""],""default"":""exact""},""valueContains"":{""type"":""string""},""category"":{""type"":""string""},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""required"":[""parameterName""],""additionalProperties"":false}"
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
            if (!string.IsNullOrWhiteSpace(categoryFilter)
                && !string.Equals(family.FamilyCategory?.Name, categoryFilter!.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var matches = new List<Dictionary<string, object?>>();
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

                matches.Add(new Dictionary<string, object?>
                {
                    ["typeId"] = symbol.Id.GetValue(),
                    ["typeName"] = symbol.Name,
                    ["parameterName"] = name,
                    ["value"] = value
                });
            }

            if (matches.Count == 0)
                continue;

            if (!families.TryGetValue(family.Id.GetValue(), out var familyMatch))
            {
                familyMatch = new FamilyMatch
                {
                    FamilyId = family.Id.GetValue(),
                    FamilyName = family.Name,
                    Category = family.FamilyCategory?.Name
                };
                families.Add(family.Id.GetValue(), familyMatch);
            }

            familyMatch.Matches.AddRange(matches);
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
                ["category"] = item.Category,
                ["matchedTypeCount"] = item.Matches.Count,
                ["matchedTypes"] = item.Matches
            });
        }

        var nextOffset = offset + page.Count;
        var payload = new Dictionary<string, object?>
        {
            ["parameterName"] = parameterName,
            ["match"] = contains ? "contains" : "exact",
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

        public required string FamilyName { get; init; }

        public string? Category { get; init; }

        public List<Dictionary<string, object?>> Matches { get; } = new();
    }
}