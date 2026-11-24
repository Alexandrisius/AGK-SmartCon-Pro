using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Groups loaded families by the signature of their shared parameters (the set
/// of shared parameter GUIDs) — "50 families from one shared parameter file,
/// 20 from another" (external benchmark v2, case 2). Two passes like
/// <see cref="FindFamiliesWithParameterTool"/>: FamilySymbol for type-scope
/// parameters, FamilyInstance for instance-scope parameters and
/// placedInstanceCount. Read-only.
/// </summary>
public sealed class GroupFamiliesBySharedParametersTool : AgentReadToolBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;
    private const int MaxFamiliesPerGroup = 100;
    private const int DefaultFamilyIdsPerGroup = 100;
    private const int MaxFamilyIdsPerGroup = 500;
    private const int MaxUnplaced = 200;

    public GroupFamiliesBySharedParametersTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "group_families_by_shared_parameters",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Groups loaded families by shared parameters (ФОП). groupBy=signature (default) uses " +
            "the exact full set of shared parameter GUIDs each family carries. That set is " +
            "strict: families from one shared parameter file may split into many groups when " +
            "their parameter subsets differ (dozens of groups on a few hundred families is " +
            "normal, not one FOP per group). For 'how many FOPs / how many families to port' " +
            "use groupBy=prefix: it buckets families by the parameter-name prefix (PREFIX_ " +
            "style, the part up to the first '_') — the practical source-level approximation, " +
            "because Revit does not store the source ФОП path. A family with several prefixes " +
            "appears in several prefix groups. Scope: type, instance or both (default, union). " +
            "Also lists unplaced families separately (includeUnplaced). Families without any " +
            "shared parameters are counted in familiesWithoutSharedParameters. compact=true " +
            "(alias groupsCompact) returns one row per group (signatureDigest/prefix, " +
            "parameterNames, parameterCount, familyCount, familyIds) instead of full parameter " +
            "rows and family cards; read family cards with get_family_parameters(familyIds:[...]). " +
            "familiesScanned is the number of distinct families; typesScanned is the number " +
            "of family symbols (types) scanned, so familiesScanned < typesScanned is normal. " +
            "familyIdsOffset/familyIdsLimit page the ids inside every group (default 100, max 500); " +
            "familyIdsReturned/familyIdsNote say how many ids are shown (for example 'familyIds " +
            "shown: 95 of 173 (offset 99)'); familyCount stays the full count, and non-compact " +
            "rows carry familiesReturned/familiesNote.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""categories"":{""type"":""array"",""items"":{""type"":""string""}},""scope"":{""type"":""string"",""enum"":[""type"",""instance"",""both""],""default"":""both""},""groupBy"":{""type"":""string"",""enum"":[""signature"",""prefix""],""default"":""signature"",""description"":""signature = exact full GUID set; prefix = source-level bucket by parameter-name prefix (recommended for how-many-FOPs questions)""},""includeUnplaced"":{""type"":""boolean"",""default"":true},""compact"":{""type"":""boolean"",""default"":false},""groupsCompact"":{""type"":""boolean"",""default"":false,""description"":""Alias for compact: one row per group without full cards""},""familyIdsOffset"":{""type"":""integer"",""default"":0,""minimum"":0,""description"":""Compact mode: skip this many family ids in every group (page through a large group)""},""familyIdsLimit"":{""type"":""integer"",""default"":100,""minimum"":1,""maximum"":500,""description"":""Compact mode: family ids per group (at most 500)""},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var categories = AgentJson.GetStringArray(args, "categories");
        var scopeText = AgentJson.GetString(args, "scope");
        var includeType = !string.Equals(scopeText, "instance", StringComparison.OrdinalIgnoreCase);
        var includeInstance = !string.Equals(scopeText, "type", StringComparison.OrdinalIgnoreCase);
        var includeUnplaced = AgentJson.GetBool(args, "includeUnplaced", true);
        var groupByText = (AgentJson.GetString(args, "groupBy") ?? "signature")
            .Trim().ToLowerInvariant();
        if (groupByText != "signature" && groupByText != "prefix")
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "groupBy must be signature or prefix. [Action: use prefix for a source-level " +
                "FOP count, signature for the exact GUID set]");
        }

        var prefixMode = groupByText == "prefix";
        var compact = AgentJson.GetBool(args, "compact", false)
                      || AgentJson.GetBool(args, "groupsCompact", false);
        var familyIdsOffset = AgentPaging.NormalizeOffset(
            AgentJson.GetInt(args, "familyIdsOffset", 0));
        var familyIdsLimit = Math.Max(
            1,
            Math.Min(
                AgentJson.GetInt(args, "familyIdsLimit", DefaultFamilyIdsPerGroup),
                MaxFamilyIdsPerGroup));
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);

        var unknownCategories = new List<string>();
        var categoryIds = AgentCategoryResolver.Resolve(doc, categories, unknownCategories);
        if (categories.Count > 0 && categoryIds.Count == 0)
        {
            var missed = unknownCategories.Count > 0 ? unknownCategories : categories;
            return ToolResult.Fail(
                "unknown_category",
                "None of the requested categories matched: " + string.Join(", ", missed) +
                AgentCategoryResolver.SuggestSuffix(doc, missed[0]) +
                ". [Action: use a category name from list_categories or an OST_* built-in name]");
        }

        var categoryIdSet = new HashSet<long>();
        foreach (var id in categoryIds)
            categoryIdSet.Add(id.GetValue());

        var resolver = new AgentSharedParameterResolver(doc);
        var families = new Dictionary<long, FamilySignature>();
        var familiesScanned = new HashSet<long>();
        var typesScanned = 0;

        // Pass 1: family symbols — type-scope shared parameters (and the family
        // registry: unplaced families appear here only).
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilySymbol symbol)
                continue;

            var family = symbol.Family;
            if (family is null || !MatchesCategory(family, categoryIdSet))
                continue;

            typesScanned++;
            familiesScanned.Add(family.Id.GetValue());
            var signature = GetOrCreate(families, family);
            if (!includeType)
                continue;

            foreach (Parameter parameter in symbol.Parameters)
                signature.AddTypeGuid(resolver.ResolveGuid(parameter));
        }

        // Pass 2: placed instances — instance-scope shared parameters and the
        // placedInstanceCount (also for scope=type, so unplacedFamilies is exact).
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not FamilyInstance instance)
                continue;

            var family = instance.Symbol?.Family;
            if (family is null || !MatchesCategory(family, categoryIdSet))
                continue;

            familiesScanned.Add(family.Id.GetValue());
            var signature = GetOrCreate(families, family);
            signature.PlacedInstanceCount++;

            if (!includeInstance)
                continue;

            foreach (Parameter parameter in instance.Parameters)
                signature.AddInstanceGuid(resolver.ResolveGuid(parameter));
        }

        var ordered = new List<FamilySignature>(families.Values);
        ordered.Sort((left, right) =>
        {
            var byName = string.Compare(left.FamilyName, right.FamilyName, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : left.FamilyId.CompareTo(right.FamilyId);
        });

        var considered = includeUnplaced
            ? ordered
            : ordered.Where(item => item.PlacedInstanceCount > 0).ToList();

        var groups = new Dictionary<string, List<FamilySignature>>(
            prefixMode ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var item in considered)
        {
            if (prefixMode)
            {
                foreach (var prefix in PrefixesOf(resolver, item, includeType, includeInstance))
                {
                    if (!groups.TryGetValue(prefix, out var prefixGroup))
                    {
                        prefixGroup = new List<FamilySignature>();
                        groups.Add(prefix, prefixGroup);
                    }

                    prefixGroup.Add(item);
                }

                continue;
            }

            var digest = item.ComputeDigest(includeType, includeInstance);
            if (!groups.TryGetValue(digest, out var group))
            {
                group = new List<FamilySignature>();
                groups.Add(digest, group);
            }

            group.Add(item);
        }

        var orderedGroups = groups
            .OrderByDescending(pair => pair.Value.Count)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        var page = AgentPaging.Slice(orderedGroups, offset, limit);
        var hasMore = offset + page.Count < orderedGroups.Count;
        var familiesWithoutShared = considered.Count(item =>
            item.SortedGuids(includeType, includeInstance).Count == 0);

        var groupRows = new List<Dictionary<string, object?>>(page.Count);
        foreach (var pair in page)
        {
            var members = pair.Value;
            var familiesReturned = Math.Min(members.Count, MaxFamiliesPerGroup);
            var familyIds = members
                .Skip(familyIdsOffset)
                .Take(familyIdsLimit)
                .Select(item => item.FamilyId)
                .ToList();
            var truncated = members.Count > familiesReturned;
            var familiesNote = truncated
                ? "families shown: " + familiesReturned + " of " + members.Count
                : null;
            var familyIdsTruncated = familyIdsOffset + familyIds.Count < members.Count;
            var familyIdsNote = familyIdsTruncated || familyIdsOffset > 0
                ? "familyIds shown: " + familyIds.Count + " of " + members.Count +
                  " (offset " + familyIdsOffset + ")"
                : null;
            if (prefixMode)
            {
                var prefixNames = DescribePrefixNames(
                    resolver, members, pair.Key, includeType, includeInstance);
                if (compact)
                {
                    groupRows.Add(new Dictionary<string, object?>
                    {
                        ["prefix"] = pair.Key,
                        ["parameterNames"] = prefixNames,
                        ["parameterCount"] = prefixNames.Count,
                        ["familyCount"] = members.Count,
                        ["familyIds"] = familyIds,
                        ["familyIdsOffset"] = familyIdsOffset,
                        ["familyIdsReturned"] = familyIds.Count,
                        ["familyIdsTruncated"] = familyIdsTruncated,
                        ["familyIdsNote"] = familyIdsNote,
                        ["familiesTruncated"] = truncated
                    });
                    continue;
                }

                groupRows.Add(new Dictionary<string, object?>
                {
                    ["prefix"] = pair.Key,
                    ["parameterNames"] = prefixNames,
                    ["parameterCount"] = prefixNames.Count,
                    ["count"] = members.Count,
                    ["families"] = BuildFamilyRows(members, familiesReturned),
                    ["familiesReturned"] = familiesReturned,
                    ["familiesTruncated"] = truncated,
                    ["familiesNote"] = familiesNote
                });
                continue;
            }

            var guids = members[0].SortedGuids(includeType, includeInstance);
            if (compact)
            {
                var names = DescribeParameters(
                        resolver, members, includeType, includeInstance)
                    .Select(row => row.GetValueOrDefault("name") as string)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToList();
                groupRows.Add(new Dictionary<string, object?>
                {
                    ["signatureDigest"] = pair.Key,
                    ["parameterNames"] = names,
                    ["parameterCount"] = names.Count,
                    ["familyCount"] = members.Count,
                    ["familyIds"] = familyIds,
                    ["familyIdsOffset"] = familyIdsOffset,
                    ["familyIdsReturned"] = familyIds.Count,
                    ["familyIdsTruncated"] = familyIdsTruncated,
                    ["familyIdsNote"] = familyIdsNote,
                    ["familiesTruncated"] = truncated
                });
                continue;
            }

            groupRows.Add(new Dictionary<string, object?>
            {
                ["signatureDigest"] = pair.Key,
                ["guids"] = guids,
                ["parameters"] = DescribeParameters(resolver, members, includeType, includeInstance),
                ["count"] = members.Count,
                ["families"] = BuildFamilyRows(members, familiesReturned),
                ["familiesReturned"] = familiesReturned,
                ["familiesTruncated"] = truncated,
                ["familiesNote"] = familiesNote
            });
        }

        var unplacedRows = new List<Dictionary<string, object?>>();
        var unplacedTotal = 0;
        if (includeUnplaced)
        {
            foreach (var item in ordered)
            {
                if (item.PlacedInstanceCount > 0)
                    continue;

                unplacedTotal++;
                // In compact mode the group rows already carry familyIds; duplicating
                // the same cards in unplacedFamilies was half of the 215k answer.
                if (compact || unplacedRows.Count >= MaxUnplaced)
                    continue;

                unplacedRows.Add(new Dictionary<string, object?>
                {
                    ["familyId"] = item.FamilyId,
                    ["familyName"] = item.FamilyName,
                    ["category"] = item.Category,
                    ["groupDigest"] = item.ComputeDigest(includeType, includeInstance)
                });
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["scope"] = scopeText is null ? "both" : scopeText.Trim().ToLowerInvariant(),
            ["groupBy"] = groupByText,
            ["includeUnplaced"] = includeUnplaced,
            ["compact"] = compact,
            ["groupCount"] = orderedGroups.Count,
            ["groups"] = groupRows,
            ["returned"] = groupRows.Count,
            ["offset"] = offset,
            ["nextOffset"] = hasMore ? offset + groupRows.Count : (int?)null,
            ["truncated"] = hasMore,
            ["total"] = considered.Count,
            ["familiesScanned"] = familiesScanned.Count,
            ["typesScanned"] = typesScanned,
            ["familiesWithoutSharedParameters"] = familiesWithoutShared,
            ["unplacedFamilies"] = unplacedRows.Count > 0 ? unplacedRows : null,
            ["unplacedCount"] = unplacedTotal,
            ["unplacedFamiliesSuppressed"] = compact && unplacedTotal > 0 ? true : null,
            ["unplacedTruncated"] = !compact && unplacedTotal > unplacedRows.Count,
            ["unknownCategories"] = unknownCategories.Count > 0 ? unknownCategories : null,
            ["scopeNote"] =
                "Type-scope GUIDs are read from family symbols, instance-scope from placed " +
                "instances. A family with no placed instances may still carry instance-scope " +
                "parameters in its document — check get_family_parameters for an exact answer. " +
                "familiesWithoutSharedParameters counts within the selected scope: with " +
                "scope=type a family that only has instance-scope shared parameters counts " +
                "as without shared." +
                (compact
                    ? " Compact mode: groups carry parameterNames and familyIds; call " +
                      "get_family_parameters(familyIds:[...]) for family cards."
                    : string.Empty) +
                (prefixMode
                    ? " groupBy=prefix is a source-level heuristic: the bucket is the parameter-name " +
                      "prefix up to the first '_'; Revit does not store the source ФОП path, and a " +
                      "family with several prefixes appears in several groups."
                    : string.Empty)
        };

        var messageText =
            "Grouped " + considered.Count + " family(ies) by " +
            (prefixMode ? "shared-parameter prefix" : "shared-parameter signature") +
            " into " + orderedGroups.Count + " group(s); returned " + groupRows.Count + "." +
            (prefixMode ? " A family may appear in several prefix groups." : string.Empty) +
            (familiesWithoutShared > 0
                ? " " + familiesWithoutShared + " family(ies) have no shared parameters."
                : string.Empty) +
            (unplacedTotal > 0
                ? " " + unplacedTotal + " family(ies) have no placed instances."
                : string.Empty) +
            (hasMore ? " Next offset " + (offset + groupRows.Count) + "." : string.Empty);

        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
    }

    private static IEnumerable<string> PrefixesOf(
        AgentSharedParameterResolver resolver,
        FamilySignature family,
        bool includeType,
        bool includeInstance)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in family.SortedGuids(includeType, includeInstance))
        {
            var prefix = AgentSharedParameterResolver.PrefixOf(
                resolver.NameOf(guid));
            if (seen.Add(prefix))
                yield return prefix;
        }
    }

    private static List<string> DescribePrefixNames(
        AgentSharedParameterResolver resolver,
        IReadOnlyList<FamilySignature> members,
        string prefix,
        bool includeType,
        bool includeInstance)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            foreach (var guid in member.SortedGuids(includeType, includeInstance))
            {
                var name = resolver.NameOf(guid);
                if (string.Equals(
                        AgentSharedParameterResolver.PrefixOf(name),
                        prefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }

        return names.ToList();
    }

    private static List<Dictionary<string, object?>> BuildFamilyRows(
        IReadOnlyList<FamilySignature> members,
        int count)
    {
        var rows = new List<Dictionary<string, object?>>(count);
        for (var index = 0; index < count; index++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["familyId"] = members[index].FamilyId,
                ["familyName"] = members[index].FamilyName,
                ["category"] = members[index].Category,
                ["placedInstanceCount"] = members[index].PlacedInstanceCount
            });
        }

        return rows;
    }

    private static List<Dictionary<string, object?>> DescribeParameters(
        AgentSharedParameterResolver resolver,
        IReadOnlyList<FamilySignature> members,
        bool includeType,
        bool includeInstance)
    {
        var guids = members[0].SortedGuids(includeType, includeInstance);
        var rows = new List<Dictionary<string, object?>>(guids.Count);
        foreach (var guid in guids)
        {
            var isType = includeType && members.Any(item => item.TypeGuids.Contains(guid));
            var isInstance = includeInstance && members.Any(item => item.InstanceGuids.Contains(guid));
            var scope = isType && isInstance
                ? "type+instance"
                : isInstance ? "instance" : "type";

            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = resolver.NameOf(guid),
                ["guid"] = guid,
                ["scope"] = scope
            });
        }

        return rows;
    }

    private static FamilySignature GetOrCreate(
        Dictionary<long, FamilySignature> families,
        Autodesk.Revit.DB.Family family)
    {
        var id = family.Id.GetValue();
        if (families.TryGetValue(id, out var existing))
            return existing;

        var created = new FamilySignature
        {
            FamilyId = id,
            FamilyName = family.Name,
            Category = family.FamilyCategory?.Name
        };
        families.Add(id, created);
        return created;
    }

    private static bool MatchesCategory(
        Autodesk.Revit.DB.Family family,
        HashSet<long> categoryIds) =>
        categoryIds.Count == 0
        || (family.FamilyCategory is not null
            && categoryIds.Contains(family.FamilyCategory.Id.GetValue()));

    private sealed class FamilySignature
    {
        public long FamilyId { get; init; }

        public required string FamilyName { get; init; }

        public string? Category { get; init; }

        public int PlacedInstanceCount { get; set; }

        public HashSet<string> TypeGuids { get; } = new(StringComparer.Ordinal);

        public HashSet<string> InstanceGuids { get; } = new(StringComparer.Ordinal);

        public void AddTypeGuid(string? guid)
        {
            if (guid is not null)
                TypeGuids.Add(guid);
        }

        public void AddInstanceGuid(string? guid)
        {
            if (guid is not null)
                InstanceGuids.Add(guid);
        }

        public List<string> SortedGuids(bool includeType, bool includeInstance)
        {
            var union = new SortedSet<string>(StringComparer.Ordinal);
            if (includeType)
                union.UnionWith(TypeGuids);
            if (includeInstance)
                union.UnionWith(InstanceGuids);
            return new List<string>(union);
        }

        public string ComputeDigest(bool includeType, bool includeInstance) =>
            ComputeDigest(SortedGuids(includeType, includeInstance));

        private static string ComputeDigest(IReadOnlyList<string> guids)
        {
            // SHA256.HashData is .NET 5+; the shared source must run on net48 too.
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", guids)));
            var builder = new StringBuilder(16);
            for (var index = 0; index < 8; index++)
                builder.Append(hash[index].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }
}
