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

    private const int MaxParameterNameRows = 100;

    private static readonly string[] FamilyRowFields =
    {
        "familyId", "familyName", "familyUniqueId", "category", "matchedParameterCount",
        "sample", "sampleTruncated", "scopes", "scopeVerification", "scopeVerifyError",
        "matchedTypeCount", "matchedTypesReturned",
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
            "different questions and are not interchangeable. " +
            "Every match row (and the per-family scopes[] field) carries parameterScope: " +
            "\"family\" (only the family document explains the match — nothing binds it at " +
            "project level), \"project\" (a project-level binding; for verified rows the family " +
            "document does NOT contain the parameter — get_family_parameters will not find it, do " +
            "not plan a family migration for it) or \"family+project\" (the shared definition is " +
            "embedded in the family AND bound in the project). A project-bound GUID shows on every " +
            "element of the bound categories whether or not the family embeds it, so family+project " +
            "is an ASSUMPTION until verified: verifyFamilyScope=true (the default) opens each " +
            "candidate family document and matches by GUID (fallback by name), turning the " +
            "assumption into verified family+project or project; rows carry scopeVerification " +
            "(verified/unverified/failed) and match rows carry familyDocument=true/false. " +
            "Verification stops at verificationLimit (default 25, max 200 documents) OR the " +
            "verificationSeconds time budget (default 120 s) — whatever comes first; when " +
            "scopeVerification.truncated=true, continue with " +
            "verificationOffset=scopeVerification.nextVerificationOffset. " +
            "The parameterScope filter is applied to the FINAL (verified) scopes. " +
            "category accepts one name or an array (display name, OST_* or id); unknown names in an " +
            "array are skipped with a warning while the valid ones filter. " +
            "requireShared=true keeps only shared (GUID-bearing) parameters — protection against " +
            "same-name non-shared visibility checkboxes that an automatic " +
            "name-based replace would corrupt. summary adds byParameterName[] " +
            "(name, guid, isShared, families, scopes, verifiedFamilies) — the per-name migration " +
            "inventory in one call instead of paging sample[] of 3.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson +
            @"""parameterName"":{""type"":""string"",""description"":""Parameter name or prefix to search for""},""parameterNamePrefix"":{""type"":""string"",""description"":""Alias for a prefix search: same as parameterName + match=prefix""},""match"":{""type"":""string"",""enum"":[""exact"",""contains"",""prefix""],""default"":""exact""},""valueContains"":{""type"":""string""},""category"":{""anyOf"":[{""type"":""string""},{""type"":""array"",""items"":{""type"":""string""}}],""description"":""Category name, OST_* built-in name or category id; an array matches any of them. Unknown names in an array are skipped with a warning""},""includeInstanceParameters"":{""type"":""boolean"",""default"":false},""requireShared"":{""type"":""boolean"",""default"":false,""description"":""Only shared parameters (with a GUID): filters out same-name family/built-in parameters such as non-shared visibility checkboxes""},""parameterScope"":{""type"":""string"",""enum"":[""any"",""family"",""family+"",""project"",""family+project""],""default"":""any"",""description"":""Filter by where the matched parameter lives (applied to the VERIFIED scopes): family (in the family document only), family+ (family OR family+project — RECOMMENDED for migration planning: every parameter that lives in family documents, including shared definitions also bound in the project), project (a project-level binding only), family+project (embedded AND bound)""},""verifyFamilyScope"":{""type"":""boolean"",""default"":true,""description"":""Open each candidate family document (EditFamily) and prove the assumed family+project scopes: parameter found -> family+project (verified), not found -> project (a project binding alone produced the match). Bounded by verificationLimit; verifyFamilyScope=false keeps fast assumed scopes""},""verificationLimit"":{""type"":""integer"",""default"":25,""minimum"":1,""maximum"":200,""description"":""Max family documents opened per call for scope verification""},""verificationSeconds"":{""type"":""integer"",""default"":120,""minimum"":1,""maximum"":150,""description"":""Time budget of the verification pass in seconds (1..150): heavy vendor .rfa take ~1 s per open, so the pass stops at the deadline and reports nextVerificationOffset instead of timing out. The budget is a limit, not a no-limit switch: the tool timeout is 180 s, so values above 150 would time the call out first""},""verificationOffset"":{""type"":""integer"",""default"":0,""description"":""Skip the first N verification candidates (page the verification together with the result pages)""},""summary"":{""type"":""boolean"",""default"":false,""description"":""Adds byParameterName[] (name, guid, isShared, families, scopes, verifiedFamilies), byCategory[] (matched families), familiesWithoutAnyShared, byPrefix[], unplacedCount""},""groupByPrefix"":{""type"":""boolean"",""default"":false,""description"":""Alias for summary: add byCategory[], byPrefix[] and familiesWithoutAnyShared""},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""maxMatchedTypes"":{""type"":""integer"",""default"":10,""minimum"":1,""maximum"":50},""compact"":{""type"":""boolean"",""default"":false},""fields"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Row keys to return: familyId, familyName, familyUniqueId, category, matchedParameterCount, sample, sampleTruncated, scopes, scopeVerification, scopeVerifyError, matchedTypeCount, matchedTypesReturned, matchedTypesTruncated, matchedTypeNames, matchedTypes, placedInstanceCount, instancesScanned, instanceParametersUnknown, matchedInstanceParameterCount, matchedInstanceParametersReturned, matchedInstanceParametersTruncated, matchedInstanceParameterNames, matchedInstanceParameters. matchedParameterCount = distinct matched parameter names; matchedTypeCount = type-level matches (type + parameter name). Unknown names return invalid_arguments.""},""offset"":{""type"":""integer"",""default"":0}},""required"":[],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var queryError = ResolveQuery(args, out var parameterName, out var mode, out var modeText);
        if (queryError is not null)
            return ToolResult.Fail("invalid_arguments", queryError);

        var valueContains = AgentJson.GetString(args, "valueContains");
        var includeInstanceParameters = AgentJson.GetBool(args, "includeInstanceParameters", false);
        var requireShared = AgentJson.GetBool(args, "requireShared", false);
        var scopeFilterError = ResolveScopeFilter(
            AgentJson.GetString(args, "parameterScope"),
            out var parameterScopeFilter);
        if (scopeFilterError is not null)
            return ToolResult.Fail("invalid_arguments", scopeFilterError);
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
        var unknownCategories = new List<string>();
        var categoryNames = AgentCategoryResolver.ReadArgument(args);
        if (categoryNames.Count > 0)
        {
            var resolved = AgentCategoryResolver.Resolve(doc, categoryNames, unknownCategories);
            if (resolved.Count == 0)
            {
                return ToolResult.Fail(
                    "unknown_category",
                    "Unknown categories: " + string.Join(", ", unknownCategories) +
                    AgentCategoryResolver.SuggestSuffix(doc, unknownCategories[0]) + ". " +
                    "[Action: pass a category name from list_categories or an OST_* built-in name]");
            }

            // A multi-category filter keeps what resolves: one unknown name
            // used to reject the whole call (live run v15).
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
            requireShared,
            ct);

        // Verification (EditFamily per candidate family, capped) turns the
        // assumed "family+project" into the proven "family+project"/"project";
        // the scope filter runs on the final scopes afterwards.
        var verifyFamilyScope = AgentJson.GetBool(args, "verifyFamilyScope", true);
        var verificationLimit = Math.Max(
            1,
            Math.Min(
                AgentJson.GetInt(args, "verificationLimit", DefaultVerificationLimit),
                MaxVerificationLimit));
        var verificationOffset = AgentPaging.NormalizeOffset(
            AgentJson.GetInt(args, "verificationOffset", 0));
        var verificationSeconds = Math.Max(
            1,
            Math.Min(
                AgentJson.GetInt(args, "verificationSeconds", DefaultVerificationSeconds),
                MaxVerificationSeconds));
        var warnings = new List<string>();
        if (verifyFamilyScope)
            VerifyFamilyScopes(
                doc, outcome, verificationLimit, verificationOffset, verificationSeconds,
                warnings, ct);
        ApplyScopeFilter(outcome, parameterScopeFilter);

        var matched = outcome.Families.Values
            .Where(item => item.HasMatches)
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
                    " family(ies) without any shared parameters" +
                    (payload.GetValueOrDefault("byParameterName") is List<Dictionary<string, object?>> names
                        ? ", " + names.Count + " matched parameter name(s) in byParameterName"
                        : string.Empty)
                  : string.Empty) +
              (!string.Equals(parameterScopeFilter, "any", StringComparison.OrdinalIgnoreCase)
                  ? "; parameterScope filter '" + parameterScopeFilter + "' applied"
                  : string.Empty) +
              (requireShared ? "; requireShared=true (GUID-bearing parameters only)" : string.Empty) +
              (verifyFamilyScope && outcome.ScopeVerificationStats is { } stats
                  ? "; scope verified against family documents for " + stats.Verified + " of " +
                    stats.Candidates + " candidate famil(ies)" +
                    (stats.Failed > 0 ? ", " + stats.Failed + " failed to open" : string.Empty) +
                    (stats.Unverified > 0
                        ? ", " + stats.Unverified + " left unverified [Action: continue with " +
                          "verificationOffset=" + stats.NextOffset + "]"
                        : string.Empty)
                  : "; scopes are ASSUMED (verifyFamilyScope=false)") +
              (unknownCategories.Count > 0
                  ? "; unknown categories ignored: " + string.Join(", ", unknownCategories)
                  : string.Empty) +
              ".";

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
                ["requireShared"] = requireShared,
                ["parameterScope"] = parameterScopeFilter,
                ["verifyFamilyScope"] = verifyFamilyScope,
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
                ["unknownCategories"] = unknownCategories.Count > 0 ? unknownCategories : null,
                ["warnings"] = warnings.Count > 0 ? warnings : null,
                ["scopeVerification"] = BuildScopeVerification(outcome, verifyFamilyScope),
                ["scopeNote"] =
                    "Type parameters are read from family symbols. With includeInstanceParameters=true " +
                    "instance-scope parameters are read from placed instances; a family with no " +
                    "placed instances can only be checked by opening its document with " +
                    "get_family_parameters. parameterScope tells WHERE a matched parameter lives: " +
                    "'family' = only the family document explains the match (nothing binds it at " +
                    "project level); 'project' = a project-level binding — for verified rows the " +
                    "family document does NOT contain the parameter, so do not plan a family " +
                    "migration for it; 'family+project' = embedded AND bound — a scopeVerification " +
                    "of 'verified' proves it against the opened family document, otherwise it is an " +
                    "ASSUMPTION from the project binding alone (a project-bound GUID shows on every " +
                    "element of the bound categories whether or not the family embeds it)."
            };

            if (!fieldSelection.IsDefault)
                result["fields"] = fieldSelection.Fields;
            if (summaryFields is not null)
            {
                foreach (var pair in summaryFields)
                    result[pair.Key] = pair.Value;
                result["byPrefixNote"] =
                    "byPrefix counts EVERY scanned family (the project-wide prefix landscape) " +
                    "and ignores parameterScope/requireShared; total and byParameterName " +
                    "describe the FILTERED matches.";
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
            ["scopes"] = item.ParameterScopes,
            ["scopeVerification"] = item.ScopeVerification,
            ["scopeVerifyError"] = item.ScopeVerifyError,
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

    private static string? ResolveScopeFilter(string? filter, out string normalized)
    {
        normalized = "any";
        if (string.IsNullOrWhiteSpace(filter))
            return null;

        var trimmed = filter!.Trim().ToLowerInvariant();
        normalized = trimmed switch
        {
            "family+project" or "project+family" => "family+project",
            _ => trimmed
        };

        return normalized is "any" or "family" or "family+" or "project" or "family+project"
            ? null
            : "parameterScope '" + filter + "' is not supported. [Action: pass any, family, family+, " +
              "project or family+project]";
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
