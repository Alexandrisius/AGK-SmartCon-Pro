using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

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
            "Groups loaded families by the signature of their shared parameters (ФОП): the set " +
            "of shared parameter GUIDs each family carries, so families from the same shared " +
            "parameter file land in one group ('50 families with one ФОП, 20 with another'). " +
            "Scope: type (parameters read from family symbols), instance (from placed " +
            "instances) or both (default, union). Also lists unplaced families separately " +
            "(includeUnplaced). Families without any shared parameters form their own group. " +
            "Use it before mass migration to shared parameters of a customer and to plan " +
            "batch work by source parameter sets.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""categories"":{""type"":""array"",""items"":{""type"":""string""}},""scope"":{""type"":""string"",""enum"":[""type"",""instance"",""both""],""default"":""both""},""includeUnplaced"":{""type"":""boolean"",""default"":true},""limit"":{""type"":""integer"",""default"":100,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var categories = AgentJson.GetStringArray(args, "categories");
        var scopeText = AgentJson.GetString(args, "scope");
        var includeType = !string.Equals(scopeText, "instance", StringComparison.OrdinalIgnoreCase);
        var includeInstance = !string.Equals(scopeText, "type", StringComparison.OrdinalIgnoreCase);
        var includeUnplaced = AgentJson.GetBool(args, "includeUnplaced", true);
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
                AgentCategoryResolver.SuggestSuffix(doc, missed.First()) +
                ". [Action: use a category name from list_categories or an OST_* built-in name]");
        }

        var categoryIdSet = new HashSet<long>();
        foreach (var id in categoryIds)
            categoryIdSet.Add(id.GetValue());

        var resolver = new SharedGuidResolver(doc);
        var families = new Dictionary<long, FamilySignature>();
        var familiesScanned = 0;

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

            familiesScanned++;
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

        var groups = new Dictionary<string, List<FamilySignature>>(StringComparer.Ordinal);
        foreach (var item in considered)
        {
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

        var groupRows = new List<Dictionary<string, object?>>(page.Count);
        foreach (var pair in page)
        {
            var members = pair.Value;
            var guids = members[0].SortedGuids(includeType, includeInstance);
            var familiesReturned = Math.Min(members.Count, MaxFamiliesPerGroup);
            var familyRows = new List<Dictionary<string, object?>>(familiesReturned);
            for (var index = 0; index < familiesReturned; index++)
            {
                familyRows.Add(new Dictionary<string, object?>
                {
                    ["familyId"] = members[index].FamilyId,
                    ["familyName"] = members[index].FamilyName,
                    ["category"] = members[index].Category,
                    ["placedInstanceCount"] = members[index].PlacedInstanceCount
                });
            }

            groupRows.Add(new Dictionary<string, object?>
            {
                ["signatureDigest"] = pair.Key,
                ["guids"] = guids,
                ["parameters"] = DescribeParameters(doc, resolver, members, includeType, includeInstance),
                ["count"] = members.Count,
                ["families"] = familyRows,
                ["familiesTruncated"] = members.Count > familiesReturned
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
                if (unplacedRows.Count >= MaxUnplaced)
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
            ["includeUnplaced"] = includeUnplaced,
            ["groupCount"] = orderedGroups.Count,
            ["groups"] = groupRows,
            ["returned"] = groupRows.Count,
            ["offset"] = offset,
            ["nextOffset"] = hasMore ? offset + groupRows.Count : (int?)null,
            ["truncated"] = hasMore,
            ["total"] = considered.Count,
            ["familiesScanned"] = familiesScanned,
            ["unplacedFamilies"] = unplacedRows.Count > 0 ? unplacedRows : null,
            ["unplacedCount"] = unplacedTotal,
            ["unplacedTruncated"] = unplacedTotal > unplacedRows.Count,
            ["unknownCategories"] = unknownCategories.Count > 0 ? unknownCategories : null,
            ["scopeNote"] =
                "Type-scope GUIDs are read from family symbols, instance-scope from placed " +
                "instances. A family with no placed instances may still carry instance-scope " +
                "parameters in its document — check get_family_parameters for an exact answer."
        };

        var messageText =
            "Grouped " + considered.Count + " family(ies) into " + orderedGroups.Count +
            " shared-parameter signature group(s); returned " + groupRows.Count + "." +
            (unplacedTotal > 0
                ? " " + unplacedTotal + " family(ies) have no placed instances."
                : string.Empty) +
            (hasMore ? " Next offset " + (offset + groupRows.Count) + "." : string.Empty);

        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
    }

    private static List<Dictionary<string, object?>> DescribeParameters(
        Document doc,
        SharedGuidResolver resolver,
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
                ["name"] = resolver.NameOf(doc, guid),
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

    /// <summary>
    /// Resolves parameter → shared GUID through the definition element
    /// (doc.GetElement(definition.Id) as SharedParameterElement → GuidValue)
    /// with a cache: the same definition is met once per parameter slot.
    /// </summary>
    private sealed class SharedGuidResolver
    {
        private readonly Document _doc;
        private readonly Dictionary<long, string?> _byDefinitionId = new();

        public SharedGuidResolver(Document doc)
        {
            _doc = doc;
        }

        public string? ResolveGuid(Parameter parameter)
        {
            try
            {
                // Definition.Id lives on InternalDefinition (the project-context
                // definition of built-in, project and shared parameters alike).
                if (parameter.Definition is not InternalDefinition definition)
                    return null;

                var definitionId = definition.Id.GetValue();
                if (_byDefinitionId.TryGetValue(definitionId, out var cached))
                    return cached;

                var resolved = _doc.GetElement(definition.Id) is SharedParameterElement shared
                    ? shared.GuidValue.ToString("D")
                    : null;
                _byDefinitionId.Add(definitionId, resolved);
                return resolved;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string NameOf(Document doc, string guid)
        {
            try
            {
                var parsed = Guid.ParseExact(guid, "D");
                return SharedParameterElement.Lookup(doc, parsed)?.Name ?? guid;
            }
            catch (Exception)
            {
                return guid;
            }
        }
    }
}
