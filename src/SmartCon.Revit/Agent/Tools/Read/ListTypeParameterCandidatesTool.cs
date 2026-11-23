using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Audits built-in (system) type-level parameters of the given categories and
/// lists the empty writable ones — the input for choosing a sorting/coding
/// parameter of group "Группировка и идентификация" (external benchmark v2,
/// case 7). All suitable built-in parameters are enumerated; the recommended
/// flag marks empty writable String parameters (ALL_MODEL_TYPE_COMMENTS,
/// ALL_MODEL_TYPE_MARK, ALL_MODEL_DESCRIPTION and peers). Read-only.
/// </summary>
public sealed class ListTypeParameterCandidatesTool : AgentReadToolBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int MaxTypesScanned = 500;

    public ListTypeParameterCandidatesTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_type_parameter_candidates",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        TimeoutMs = 60000,
        Description =
            "Lists built-in (system) type-level parameters of the requested categories with " +
            "their fill state across ALL types of the category: parameterName (localized label), " +
            "builtInParameter (enum name like ALL_MODEL_TYPE_COMMENTS), storageType, parameter " +
            "group (PG_IDENTITY_DATA = 'Группировка и идентификация' and others), isReadOnly, " +
            "emptyAcrossAllTypes, nonEmptyCount and totalTypes. recommended=true marks empty " +
            "writable String parameters sorted first — the safest slot for a type-level code " +
            "or sort key. Does not hardcode a parameter list; every suitable built-in is shown. " +
            "categories or categoryIds is required.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""categories"":{""type"":""array"",""items"":{""type"":""string""}},""categoryIds"":{""type"":""array"",""items"":{""type"":""integer""}},""group"":{""type"":""string"",""description"":""Optional group filter: PG_IDENTITY_DATA, PG_GENERAL, ...""},""limit"":{""type"":""integer"",""default"":50,""maximum"":200}},""anyOf"":[{""required"":[""categories""]},{""required"":[""categoryIds""]}],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var categories = AgentJson.GetStringArray(args, "categories");
        var categoryIds = AgentJson.GetLongArray(args, "categoryIds");
        var groupFilter = AgentJson.GetString(args, "group");
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);

        if (categories.Count == 0 && categoryIds.Count == 0)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "categories or categoryIds is required: pass at least one category " +
                "(name from list_categories or an OST_* built-in name). [Action: check " +
                "list_categories and repeat the call]");
        }

        var unknown = new List<string>();
        var resolved = AgentCategoryResolver.Resolve(doc, categories, unknown);
        var categoryIdSet = new HashSet<long>();
        foreach (var id in resolved)
            categoryIdSet.Add(id.GetValue());
        foreach (var raw in categoryIds)
            categoryIdSet.Add(raw);

        if (categoryIdSet.Count == 0)
        {
            var missed = unknown.Count > 0 ? unknown : categories;
            return ToolResult.Fail(
                "unknown_category",
                "None of the requested categories matched: " + string.Join(", ", missed) +
                AgentCategoryResolver.SuggestSuffix(doc, missed[0]) +
                ". [Action: use a category name from list_categories or an OST_* built-in name]");
        }

        // Single pass over the types (capped): every built-in parameter slot is
        // accumulated into per-candidate stats — presence, empty and non-empty.
        var stats = new Dictionary<long, CandidateStats>();
        var typesScanned = 0;
        var typeNames = new List<string>();
        foreach (var element in new FilteredElementCollector(doc)
                     .WhereElementIsElementType())
        {
            ct.ThrowIfCancellationRequested();
            if (element is not ElementType type || !MatchesCategory(type, categoryIdSet))
                continue;

            typesScanned++;
            if (typeNames.Count < 5)
                typeNames.Add(type.Name);

            if (typesScanned > MaxTypesScanned)
                continue;

            foreach (Parameter parameter in type.Parameters)
            {
                long builtInId;
                try
                {
                    // Definition.Id lives on InternalDefinition only.
                    if (parameter.Definition is not InternalDefinition definition)
                        continue;

                    builtInId = definition.Id.GetValue();
                }
                catch (Exception)
                {
                    continue;
                }

                if (builtInId >= 0)
                    continue;

                var candidate = GetOrCreate(stats, doc, parameter, builtInId);
                candidate.PresentCount++;
                if (IsEmpty(parameter))
                    candidate.EmptyCount++;
                else
                    candidate.NonEmptyCount++;
            }
        }

        if (typesScanned == 0)
        {
            return ToolResult.Fail(
                "no_types_found",
                "The requested categories have no element types in this document. " +
                "[Action: check the category with list_system_types or list_families]");
        }

        var candidates = new List<CandidateStats>(stats.Values);
        if (!string.IsNullOrWhiteSpace(groupFilter))
        {
            var normalized = groupFilter!.Trim();
            candidates.RemoveAll(item =>
                !string.Equals(item.Group, normalized, StringComparison.OrdinalIgnoreCase));
        }

        candidates.Sort((left, right) =>
        {
            // Recommendation order: empty writable String first, then every empty
            // writable, then the rest; identity-data group beats the others.
            var byRecommendation = right.Rank().CompareTo(left.Rank());
            if (byRecommendation != 0)
                return byRecommendation;

            var byIdentity = string.Equals(left.Group, "PG_IDENTITY_DATA", StringComparison.Ordinal)
                ? -1
                : string.Equals(right.Group, "PG_IDENTITY_DATA", StringComparison.Ordinal) ? 1 : 0;
            if (byIdentity != 0)
                return byIdentity;

            return string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        });

        var page = AgentPaging.Slice(candidates, 0, limit);
        var rows = new List<Dictionary<string, object?>>(page.Count);
        foreach (var candidate in page)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["parameterName"] = candidate.Label,
                ["builtInParameter"] = candidate.EnumName,
                ["storageType"] = candidate.StorageType,
                ["group"] = candidate.Group,
                ["isReadOnly"] = candidate.IsReadOnly,
                ["recommended"] = candidate.IsRecommended(),
                ["emptyAcrossAllTypes"] = candidate.NonEmptyCount == 0,
                ["nonEmptyCount"] = candidate.NonEmptyCount,
                ["emptyCount"] = candidate.EmptyCount,
                ["presentCount"] = candidate.PresentCount,
                ["totalTypes"] = Math.Min(typesScanned, MaxTypesScanned)
            });
        }

        var recommendedCount = candidates.Count(item => item.IsRecommended());
        var payload = new Dictionary<string, object?>
        {
            ["candidates"] = rows,
            ["returned"] = rows.Count,
            ["total"] = candidates.Count,
            ["truncated"] = candidates.Count > rows.Count,
            ["recommendedCount"] = recommendedCount,
            ["typesScanned"] = typesScanned,
            ["typesTruncated"] = typesScanned > MaxTypesScanned,
            ["unknownCategories"] = unknown.Count > 0 ? unknown : null,
            ["sampleTypeNames"] = typeNames,
            ["hint"] =
                "recommended = empty on every scanned type, writable and String storage — " +
                "safe to reuse as a type-level code/sort parameter. totalTypes is the number " +
                "of scanned types (cap " + MaxTypesScanned + ")."
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Scanned " + typesScanned + " type(s): " + candidates.Count +
            " built-in type parameter(s), " + recommendedCount + " recommended (empty writable " +
            "String); returned " + rows.Count + ".");
    }

    private static CandidateStats GetOrCreate(
        Dictionary<long, CandidateStats> stats,
        Document doc,
        Parameter parameter,
        long builtInId)
    {
        if (stats.TryGetValue(builtInId, out var existing))
            return existing;

        var builtIn = (BuiltInParameter)(int)builtInId;
        string label;
        try
        {
            label = LabelUtils.GetLabelFor(builtIn);
        }
        catch (Exception)
        {
            label = builtIn.ToString();
        }

        string group;
        try
        {
            group = AgentFamilySpecs.GroupName(parameter.Definition);
        }
        catch (Exception)
        {
            group = "unknown";
        }

        string storageType;
        bool isReadOnly;
        try
        {
            storageType = parameter.StorageType.ToString();
            isReadOnly = parameter.IsReadOnly;
        }
        catch (Exception)
        {
            storageType = "Unknown";
            isReadOnly = true;
        }

        var created = new CandidateStats
        {
            EnumName = builtIn.ToString(),
            Label = label,
            Group = group,
            StorageType = storageType,
            IsReadOnly = isReadOnly
        };
        stats.Add(builtInId, created);
        return created;
    }

    private static bool MatchesCategory(ElementType type, HashSet<long> categoryIds) =>
        type.Category is not null && categoryIds.Contains(type.Category.Id.GetValue());

    /// <summary>
    /// The SummarizeElementsTool.ReadGroupKey empty contract: HasValue=false is
    /// a real "no value", a blank string is an empty value.
    /// </summary>
    private static bool IsEmpty(Parameter parameter)
    {
        try
        {
            if (!parameter.HasValue)
                return true;

            var text = parameter.StorageType == StorageType.String
                ? parameter.AsString()
                : parameter.AsValueString();
            return string.IsNullOrWhiteSpace(text);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private sealed class CandidateStats
    {
        public required string EnumName { get; init; }

        public required string Label { get; init; }

        public required string Group { get; init; }

        public required string StorageType { get; init; }

        public bool IsReadOnly { get; init; }

        public int PresentCount { get; set; }

        public int EmptyCount { get; set; }

        public int NonEmptyCount { get; set; }

        public bool IsRecommended() =>
            !IsReadOnly
            && NonEmptyCount == 0
            && string.Equals(StorageType, "String", StringComparison.Ordinal);

        public int Rank()
        {
            if (IsRecommended())
                return 3;
            if (!IsReadOnly && NonEmptyCount == 0)
                return 2;
            return 1;
        }
    }
}
