using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Live scan (no persistent index) over loaded family types: finds families whose
/// types expose a parameter with the requested name (exact, contains or prefix)
/// and optionally a value. Instance-scope parameters live on placed instances, not
/// on the family symbol, so they need the separate
/// <c>includeInstanceParameters</c> pass. With <c>summary=true</c> the same scan
/// also reports the shared-parameter landscape (families without any shared
/// parameter and the parameter-name prefixes such as PREFIX_ / BP_ / BIM_).
/// </summary>
public sealed partial class FindFamiliesWithParameterTool : AgentReadToolBase
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

    private const int MaxSampleParameterNames = 3;

    private const int MaxPrefixBuckets = 20;

    private static readonly string[] FamilyRowFields =
    {
        "familyId", "familyName", "familyUniqueId", "category", "matchedParameterCount",
        "sample", "sampleTruncated", "matchedTypeCount", "matchedTypesReturned",
        "matchedTypesTruncated", "matchedTypeNames", "matchedTypes", "placedInstanceCount",
        "instancesScanned", "instanceParametersUnknown", "matchedInstanceParameterCount",
        "matchedInstanceParametersReturned", "matchedInstanceParametersTruncated",
        "matchedInstanceParameterNames", "matchedInstanceParameters"
    };

    /// <summary>Room for the tool envelope and the message inside the 40k budget.</summary>
    private const int PageBudgetCharacters = ToolResultBudget.DefaultMaxCharacters - 2000;

    public FindFamiliesWithParameterTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "find_families_with_parameter",
        Description =
            "Audit/migration: finds loaded families by parameter name (exact, contains or " +
            "prefix) and optional value. Use match=prefix (or parameterNamePrefix) for a " +
            "one-call inventory by shared-parameter prefix — PREFIX_, BP_, BIM_, any ФОП prefix — " +
            "instead of group_families_by_shared_parameters (that tool groups families by the " +
            "full GUID signature and returns heavy cards). summary=true (alias groupByPrefix) adds " +
            "byCategory[] over the matched families (category + families), so a category overview " +
            "does not need every page, familiesWithoutAnyShared and byPrefix[] (prefix, families, " +
            "parameters) across every scanned family, not only the matches; the prefix is the part " +
            "up to the first '_' " +
            "(e.g. PREFIX_), names without '_' fall into the '(none)' bucket. unplacedCount is " +
            "reported whenever includeInstanceParameters=true (null when instance parameters " +
            "were not scanned). " +
            "Scans the live project on every call — no index; unplaced types are included. " +
            "By default only type parameters are searched. With includeInstanceParameters=true " +
            "placed family instances are also scanned, so instance-scope shared parameters " +
            "(for example a name parameter) are found; placedInstanceCount is reported only " +
            "with includeInstanceParameters=true (null and instancesScanned=false otherwise — " +
            "not scanned, not unplaced); families with no placed instances are flagged " +
            "instanceParametersUnknown=true (their instance parameters can only be checked by " +
            "opening the family document via get_family_parameters). compact=true returns " +
            "compact rows with familyName, matchedParameterCount and a sample[] of matched " +
            "parameter names — enough for a migration plan, without full type/instance rows; " +
            "fields:[...] projects the row keys, so compact=true with " +
            "fields:[\"familyId\",\"familyName\",\"matchedParameterCount\",\"sample\"] keeps a " +
            "few-hundred-family list in one call. " +
            "matchedTypes and matchedInstanceParameters are capped per family by maxMatchedTypes " +
            "(default 10, max 50); the totals and *Truncated flags show how many matches exist " +
            "in full. matchedParameterCount counts distinct matched parameter NAMES in the " +
            "family; matchedTypeCount counts type-level matches (one per matching parameter " +
            "name in a type, so a type with two matching names counts twice) — they answer " +
            "different questions and are not interchangeable.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson +
            @"""parameterName"":{""type"":""string"",""description"":""Parameter name or prefix to search for""},""parameterNamePrefix"":{""type"":""string"",""description"":""Alias for a prefix search: same as parameterName + match=prefix""},""match"":{""type"":""string"",""enum"":[""exact"",""contains"",""prefix""],""default"":""exact""},""valueContains"":{""type"":""string""},""category"":{""type"":""string""},""includeInstanceParameters"":{""type"":""boolean"",""default"":false},""summary"":{""type"":""boolean"",""default"":false,""description"":""Adds byCategory[] (matched families), familiesWithoutAnyShared, byPrefix[], unplacedCount""},""groupByPrefix"":{""type"":""boolean"",""default"":false,""description"":""Alias for summary: add byCategory[], byPrefix[] and familiesWithoutAnyShared""},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""maxMatchedTypes"":{""type"":""integer"",""default"":10,""minimum"":1,""maximum"":50},""compact"":{""type"":""boolean"",""default"":false},""fields"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Row keys to return: familyId, familyName, familyUniqueId, category, matchedParameterCount, sample, sampleTruncated, matchedTypeCount, matchedTypesReturned, matchedTypesTruncated, matchedTypeNames, matchedTypes, placedInstanceCount, instancesScanned, instanceParametersUnknown, matchedInstanceParameterCount, matchedInstanceParametersReturned, matchedInstanceParametersTruncated, matchedInstanceParameterNames, matchedInstanceParameters. matchedParameterCount = distinct matched parameter names; matchedTypeCount = type-level matches (type + parameter name). Unknown names return invalid_arguments.""},""offset"":{""type"":""integer"",""default"":0}},""required"":[],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var queryError = ResolveQuery(args, out var parameterName, out var mode, out var modeText);
        if (queryError is not null)
            return ToolResult.Fail("invalid_arguments", queryError);

        var valueContains = AgentJson.GetString(args, "valueContains");
        var categoryFilter = AgentJson.GetString(args, "category");
        var includeInstanceParameters = AgentJson.GetBool(args, "includeInstanceParameters", false);
        var summary = AgentJson.GetBool(args, "summary", false)
                      || AgentJson.GetBool(args, "groupByPrefix", false);
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
        var fieldSelection = AgentListPayload.SelectFields(
            FamilyRowFields,
            AgentJson.GetStringArray(args, "fields"),
            FamilyRowFields);
        if (!fieldSelection.IsValid)
            return ToolResult.Fail("invalid_arguments", fieldSelection.Error!);

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

        var outcome = ScanFamilies(
            doc,
            parameterName!,
            mode,
            valueContains,
            categoryIds,
            includeInstanceParameters,
            summary,
            maxMatchedTypes,
            ct);

        var matched = outcome.Families.Values
            .Where(item => item.Matched)
            .ToList();
        matched.Sort((left, right) =>
            string.Compare(left.FamilyName, right.FamilyName, StringComparison.OrdinalIgnoreCase));

        var typeMatches = matched.Sum(item => item.MatchedTypesTotal);
        var instanceMatches = matched.Sum(item => item.MatchedInstanceParametersTotal);
        var instancesScanned = includeInstanceParameters;
        var unplacedCount = instancesScanned
            ? outcome.Families.Values.Count(item => item.PlacedInstanceCount == 0)
            : (int?)null;
        // The summary is a full pass over the scanned families; the page loop may
        // run several times, so compute it once. byCategory counts the matched
        // families (total), not the page: that is what makes a truncated page
        // unnecessary for a category overview.
        var summaryFields = summary ? BuildSummary(outcome, instancesScanned) : null;
        var byCategory = summary ? BuildByCategory(matched) : null;

        // A page of compact rows can still exceed the context budget: answer with
        // an honest smaller page and nextOffset instead of letting the generic
        // trimmer halve families[] while "returned" keeps the pre-trim count.
        var pageSize = Math.Min(limit, Math.Max(0, matched.Count - offset));
        List<Dictionary<string, object?>> page;
        Dictionary<string, object?> payload;
        while (true)
        {
            page = new List<Dictionary<string, object?>>();
            for (var index = offset; index < matched.Count && page.Count < pageSize; index++)
                page.Add(BuildFamilyRow(
                    matched[index], compact, includeInstanceParameters, fieldSelection));

            payload = BuildPayload(page);
            if (pageSize <= 1
                || AgentJson.Serialize(payload).GetRawText().Length <= PageBudgetCharacters)
            {
                break;
            }

            pageSize = Math.Max(1, pageSize / 2);
        }

        var nextOffset = offset + page.Count;
        var messageText = matched.Count == 0
            ? "No loaded family exposes a parameter matching '" + parameterName + "'. " +
              "Scanned " + outcome.TypesScanned + " type(s) in " + outcome.FamiliesSeen.Count +
              " family(ies)" +
              (includeInstanceParameters
                  ? " and " + outcome.InstancesScanned + " placed instance(s)."
                  : "; pass includeInstanceParameters=true to also scan placed instances.") +
              " Use list_families / get_family_parameters to inspect a family."
            : "Found " + matched.Count + " family(ies) with parameter '" + parameterName +
              "' (" + typeMatches + " type match(es)" +
              (includeInstanceParameters ? ", " + instanceMatches + " instance parameter(s)" : "") +
              "); returned " + page.Count +
              (nextOffset < matched.Count ? ", next offset " + nextOffset : string.Empty) +
              (payload.GetValueOrDefault("pageBudgetApplied") is true
                  ? " (the page was reduced to fit the context budget)"
                  : string.Empty) +
              (summary && payload.GetValueOrDefault("byPrefix") is List<Dictionary<string, object?>> prefixes
                  ? "; " + prefixes.Count + " shared-parameter prefix bucket(s), " +
                    payload.GetValueOrDefault("familiesWithoutAnyShared") +
                    " family(ies) without any shared parameters."
                  : string.Empty) + ".";

        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);

        Dictionary<string, object?> BuildPayload(List<Dictionary<string, object?>> currentPage)
        {
            var next = offset + currentPage.Count;
            var more = next < matched.Count;
            var result = new Dictionary<string, object?>
            {
                ["parameterName"] = parameterName,
                ["parameterNamePrefix"] = mode == MatchMode.Prefix ? parameterName : null,
                ["match"] = modeText,
                ["includeInstanceParameters"] = includeInstanceParameters,
                ["summary"] = summary,
                ["compact"] = compact,
                ["families"] = currentPage,
                ["total"] = matched.Count,
                ["returned"] = currentPage.Count,
                ["offset"] = offset,
                ["nextOffset"] = more ? next : (int?)null,
                ["truncated"] = more,
                ["pageBudgetApplied"] = currentPage.Count < limit && more ? true : null,
                ["familiesScanned"] = outcome.FamiliesSeen.Count,
                ["typesScanned"] = outcome.TypesScanned,
                ["instancesScanned"] = outcome.InstancesScanned,
                ["unplacedCount"] = unplacedCount,
                ["typeMatchesTotal"] = typeMatches,
                ["instanceParametersTotal"] = instanceMatches,
                ["scopeNote"] =
                    "Type parameters are read from family symbols. With includeInstanceParameters=true " +
                    "instance-scope parameters are read from placed instances; a family with no " +
                    "placed instances can only be checked by opening its document with " +
                    "get_family_parameters."
            };

            if (!fieldSelection.IsDefault)
                result["fields"] = fieldSelection.Fields;
            if (summaryFields is not null)
            {
                foreach (var pair in summaryFields)
                    result[pair.Key] = pair.Value;
                result["byCategory"] = byCategory;
                result["byCategoryNote"] =
                    "byCategory counts the matched families (total), not the page.";
            }

            return result;
        }
    }

    private static Dictionary<string, object?> BuildFamilyRow(
        FamilyScan item,
        bool compact,
        bool includeInstanceParameters,
        AgentListPayload.FieldSelection fields)
    {
        var row = new Dictionary<string, object?>
        {
            ["familyId"] = item.FamilyId,
            ["familyName"] = item.FamilyName,
            ["familyUniqueId"] = item.FamilyUniqueId,
            ["category"] = item.Category,
            ["matchedParameterCount"] = item.MatchedParameterNames.Count,
            ["sample"] = item.MatchedParameterNames
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSampleParameterNames)
                .ToList(),
            ["sampleTruncated"] = item.MatchedParameterNames.Count > MaxSampleParameterNames,
            ["matchedTypeCount"] = item.MatchedTypesTotal,
            ["matchedTypesReturned"] = item.TypeMatches.Count,
            ["matchedTypesTruncated"] = item.MatchedTypesTotal > item.TypeMatches.Count,
            ["matchedTypeNames"] = compact
                ? item.TypeMatches.Select(match => match["typeName"]).ToList()
                : null,
            ["matchedTypes"] = compact ? null : item.TypeMatches,
            // Null (not zero) when the instances were not scanned: a zero here
            // read as "family has no placed instances".
            ["placedInstanceCount"] = includeInstanceParameters
                ? (int?)item.PlacedInstanceCount
                : null,
            ["instancesScanned"] = includeInstanceParameters,
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
        };

        return fields.IsDefault ? row : AgentListPayload.Project(row, fields);
    }

    private static List<Dictionary<string, object?>> BuildByCategory(
        IReadOnlyList<FamilyScan> matched) =>
        matched
            .GroupBy(item => item.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Dictionary<string, object?>
            {
                ["category"] = group.Key.Length == 0 ? null : group.Key,
                ["families"] = group.Count()
            })
            .OrderByDescending(row => (int)row["families"]!)
            .ThenBy(row => (string?)row["category"], StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static Dictionary<string, object?> BuildSummary(
        ScanOutcome outcome,
        bool instancesScanned)
    {
        var families = outcome.Families.Values.ToList();
        var withoutShared = families.Count(item => item.SharedGuids.Count == 0);
        var buckets = new Dictionary<string, PrefixBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in families)
        {
            foreach (var name in family.SharedParameterNames)
            {
                var prefix = PrefixOf(name);
                if (!buckets.TryGetValue(prefix, out var bucket))
                {
                    bucket = new PrefixBucket(prefix);
                    buckets.Add(prefix, bucket);
                }

                bucket.Families.Add(family.FamilyId);
                bucket.Parameters.Add(name);
            }
        }

        var ordered = buckets.Values
            .OrderByDescending(bucket => bucket.Families.Count)
            .ThenBy(bucket => bucket.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var byPrefix = ordered
            .Take(MaxPrefixBuckets)
            .Select(bucket => new Dictionary<string, object?>
            {
                ["prefix"] = bucket.Prefix,
                ["families"] = bucket.Families.Count,
                ["parameters"] = bucket.Parameters.Count
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["familiesWithoutAnyShared"] = withoutShared,
            ["familiesWithSharedParameters"] = families.Count - withoutShared,
            ["placedFamilies"] = instancesScanned
                ? families.Count(item => item.PlacedInstanceCount > 0)
                : (int?)null,
            ["byPrefix"] = byPrefix,
            ["byPrefixTruncated"] = ordered.Count > byPrefix.Count,
            ["summaryScope"] = instancesScanned ? "type+instance" : "type",
            ["summaryNote"] =
                "familiesWithoutAnyShared and byPrefix count shared parameters only. " +
                (instancesScanned
                    ? "Scope is type+instance."
                    : "Scope is type only: pass includeInstanceParameters=true for instance-scope " +
                      "shared parameters and unplaced counts.")
        };
    }

    private static string PrefixOf(string name) =>
        AgentSharedParameterResolver.PrefixOf(name);

    private static string? ResolveQuery(
        JsonElement args,
        out string? parameterName,
        out MatchMode mode,
        out string modeText)
    {
        parameterName = AgentJson.GetString(args, "parameterName");
        var prefixAlias = AgentJson.GetString(args, "parameterNamePrefix");
        modeText = (AgentJson.GetString(args, "match") ?? "exact").Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(prefixAlias))
        {
            if (!string.IsNullOrWhiteSpace(parameterName)
                && !string.Equals(
                    parameterName!.Trim(), prefixAlias!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                mode = MatchMode.Exact;
                return "Pass parameterName or parameterNamePrefix, not both with different values.";
            }

            parameterName = prefixAlias;
            if (string.IsNullOrWhiteSpace(AgentJson.GetString(args, "match")))
                modeText = "prefix";
        }

        if (string.IsNullOrWhiteSpace(parameterName))
        {
            mode = MatchMode.Exact;
            return "parameterName (or parameterNamePrefix) is required: pass the parameter " +
                   "name or prefix to search for.";
        }

        switch (modeText)
        {
            case "exact":
                mode = MatchMode.Exact;
                return null;
            case "contains":
                mode = MatchMode.Contains;
                return null;
            case "prefix":
                mode = MatchMode.Prefix;
                return null;
            default:
                mode = MatchMode.Exact;
                return "match must be exact, contains or prefix.";
        }
    }

    private enum MatchMode
    {
        Exact,
        Contains,
        Prefix
    }

    private sealed class PrefixBucket
    {
        public PrefixBucket(string prefix)
        {
            Prefix = prefix;
        }

        public string Prefix { get; }

        public HashSet<long> Families { get; } = new();

        public HashSet<string> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
