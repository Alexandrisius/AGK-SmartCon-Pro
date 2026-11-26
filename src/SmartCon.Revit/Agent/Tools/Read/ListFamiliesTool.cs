using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the families loaded in the project. Family, family type and family
/// instance are different things: this tool returns families only.
/// </summary>
public sealed class ListFamiliesTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    /// <summary>Record field with the number of the family's own placed instances.</summary>
    private const string PlacedField = "placedInstanceCount";

    private static readonly string[] AvailableFields =
    {
        "familyId", "uniqueId", "name", "category", "categoryId",
        "typeCount", PlacedField, "isEditable", "isInPlace", "isParametric", "isShared"
    };

    private static readonly string[] DefaultFields = { "familyId", "name", "category", "typeCount" };

    private static readonly string[] TruncationWays =
    {
        "category / nameContains", "request fewer fields", "pass summary=true for counts"
    };

    private static readonly string[] TruncationWaysWithSummary =
    {
        "category / nameContains", "request fewer fields"
    };

    public ListFamiliesTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_families",
        Description =
            "Lists the families loaded in the project, filtered by name and category. " +
            "Records are compact by default (familyId, name, category, typeCount): fields " +
            "selects other record fields (placedInstanceCount = instances of the family " +
            "placed in the model; 0 means it has no instance of its own - a nested or " +
            "profile family may still be in use, so 0 does not mean unused; isShared = the " +
            "family is shared - a shared NESTED family appears here as an unplaced family " +
            "but is scheduled as a separate row; which host nests it: get_family_parameters " +
            "nestedFamilies[]), " +
            "compact=false returns all eleven, summary=true adds counts over the whole " +
            "filtered set (totalFamilies, totalTypes, byCategory, placed, unplaced, " +
            "familiesWithTypes/familiesWithoutTypes). familiesWithoutTypes are counted in " +
            "totalFamilies but are absent from symbol-based scans (group_families_by_shared_parameters, " +
            "find_families_with_parameter), so their familiesScanned can be smaller. " +
            "Families are not types and not instances: use list_family_types for the types " +
            "of a family. System families (walls, floors, pipes and the like) are not " +
            "listed here: their types come from list_system_types.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""},""nameContains"":{""type"":""string"",""description"":""Case-insensitive substring of the family name.""},""category"":{""anyOf"":[{""type"":""string""},{""type"":""array"",""items"":{""type"":""string""}}],""description"":""Category name or OST_* built-in name. A family matches any of the listed categories.""},""includeInPlace"":{""type"":""boolean"",""default"":true},""fields"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Record fields to return in compact mode. Allowed: familyId, uniqueId, name, category, categoryId, typeCount, placedInstanceCount, isEditable, isInPlace, isParametric, isShared. Default: familyId, name, category, typeCount. Unknown names return invalid_arguments.""},""compact"":{""type"":""boolean"",""default"":true,""description"":""true: each record carries only the fields listed in fields. false: every record carries all eleven fields.""},""summary"":{""type"":""boolean"",""default"":false,""description"":""Adds summary over the whole filtered set, ignoring limit/offset: totalFamilies, totalTypes, byCategory [{category, families, types}] sorted by families desc, inPlace, nonParametric, placed, unplaced (families with/without an instance of their own in the model; unplaced does not mean unused - nested and profile families have no instance of their own), familiesWithTypes, familiesWithoutTypes, familiesWithoutTypesSample, familyScanNote (families without types are absent from symbol-based family scans).""},""limit"":{""type"":""integer"",""default"":200,""maximum"":1000},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var categoryNames = AgentCategoryResolver.ReadArgument(args);
        var unknownCategories = new List<string>();
        HashSet<long>? categoryIds = null;
        if (categoryNames.Count > 0)
        {
            var resolved = AgentCategoryResolver.Resolve(doc, categoryNames, unknownCategories);
            if (resolved.Count == 0)
            {
                return ToolResult.Fail(
                    "unknown_category",
                    "Unknown categories: " + string.Join(", ", unknownCategories) +
                    AgentCategoryResolver.SuggestSuffix(doc, unknownCategories[0]) +
                    ". Use list_categories to see available names.");
            }

            // A multi-category filter keeps the categories that resolve: one
            // unknown name used to reject the WHOLE call (live run v15), which
            // looked like "arrays do not work" although each name was fine as
            // a single string.
            categoryIds = new HashSet<long>(resolved.Select(id => id.GetValue()));
        }

        var compact = AgentJson.GetBool(args, "compact", true);
        var withSummary = AgentJson.GetBool(args, "summary", false);
        var selection = AgentListPayload.SelectFields(
            AvailableFields,
            AgentJson.GetStringArray(args, "fields"),
            DefaultFields);
        if (!selection.IsValid)
            return ToolResult.Fail("invalid_arguments", selection.Error!);

        var nameContains = AgentJson.GetString(args, "nameContains");
        var includeInPlace = AgentJson.GetBool(args, "includeInPlace", true);
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);

        // The placed-instance scan is a full pass over the model: only a call
        // that shows the counts anywhere pays for it.
        var needsPlaced = NeedsPlacedCounts(compact, withSummary, selection.Fields);
        Dictionary<long, int>? placedCounts = null;
        var families = new List<Dictionary<string, object?>>();
        var summary = new InventorySummary();
        foreach (var family in AgentFamilyReader.LoadableFamilies(doc))
        {
            ct.ThrowIfCancellationRequested();

            if (!includeInPlace && family.IsInPlace)
                continue;

            if (!string.IsNullOrWhiteSpace(nameContains)
                && !AgentFamilySpecs.ContainsNormalized(family.Name, nameContains!))
            {
                continue;
            }

            if (categoryIds is not null
                && (family.FamilyCategory is null
                    || !categoryIds.Contains(family.FamilyCategory.Id.GetValue())))
            {
                continue;
            }

            var inWindow = ++summary.Total > offset && families.Count < limit;
            var categoryName = family.FamilyCategory?.Name;
            var typeCount = family.GetFamilySymbolIds().Count;
            var placedInstanceCount = 0;
            if (needsPlaced)
            {
                placedCounts ??= PlacedInstanceCounts(doc);
                placedCounts.TryGetValue(family.Id.GetValue(), out placedInstanceCount);
            }

            summary.Add(family.Name, categoryName, typeCount, family.IsInPlace, family.IsParametric,
                placedInstanceCount > 0);
            if (!inWindow)
                continue;

            var record = new Dictionary<string, object?>
            {
                ["familyId"] = family.Id.GetValue(),
                ["uniqueId"] = family.UniqueId,
                ["name"] = family.Name,
                ["category"] = categoryName,
                ["categoryId"] = family.FamilyCategory?.Id.GetValue(),
                ["typeCount"] = typeCount,
                ["placedInstanceCount"] = placedInstanceCount,
                ["isEditable"] = family.IsEditable,
                ["isInPlace"] = family.IsInPlace,
                ["isParametric"] = family.IsParametric,
                // No Family.IsShared in the API: the built-in parameter is the
                // flag (v14: shared nested families are scheduled separately).
                ["isShared"] = family.get_Parameter(BuiltInParameter.FAMILY_SHARED)
                    ?.AsInteger() == 1
            };
            families.Add(compact ? AgentListPayload.Project(record, selection) : record);
        }

        var page = AgentListPayload.BuildPage(summary.Total, offset, families.Count);
        var payload = new Dictionary<string, object?>
        {
            ["families"] = families
        };
        page.AddTo(payload);
        AgentListPayload.AddFieldInfo(
            payload,
            compact ? selection.Fields : selection.AvailableFields,
            selection.AvailableFields);
        if (withSummary)
            payload["summary"] = summary.Build();
        if (unknownCategories.Count > 0)
        {
            payload["unknownCategories"] = unknownCategories;
            payload["unknownCategoriesNote"] =
                "These category names were skipped; the rest of the filter was applied.";
        }

        var messageText = "Families: " + families.Count + " of " + summary.Total +
                          " (" + summary.CategoryCount + " categories).";
        if (unknownCategories.Count > 0)
        {
            messageText += " Unknown categories ignored: " +
                           string.Join(", ", unknownCategories) +
                           " [Action: pass names from list_categories]";
        }

        if (page.Truncated)
        {
            messageText += AgentListPayload.TruncatedHint(
                withSummary ? TruncationWaysWithSummary : TruncationWays);
        }

        if (selection.IsDefault && compact && families.Count >= AgentListPayload.CompactHintThreshold)
            messageText += AgentListPayload.CompactFieldsHint(selection.Fields);

        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
    }

    /// <summary>
    /// Whether the call shows the placed counts anywhere: the field itself,
    /// every field (compact=false) or the summary with its placed/unplaced.
    /// </summary>
    internal static bool NeedsPlacedCounts(bool compact, bool withSummary, IReadOnlyList<string> fields) =>
        !compact || withSummary || fields.Any(field =>
            string.Equals(field, PlacedField, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One collector over the placed family instances: familyId -> instance
    /// count. It counts the instances of a family's OWN - a nested or a profile
    /// family has none and stays at 0, which says nothing about being in use.
    /// A full pass over the model, so the callers that do not show the counts
    /// never ask for it (<see cref="NeedsPlacedCounts"/>).
    /// </summary>
    private static Dictionary<long, int> PlacedInstanceCounts(Document doc)
    {
        var counts = new Dictionary<long, int>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
        {
            if (element is not FamilyInstance instance
                || instance.Symbol is null
                || instance.Symbol.Family is null)
            {
                continue;
            }

            var familyId = instance.Symbol.Family.Id.GetValue();
            counts.TryGetValue(familyId, out var count);
            counts[familyId] = count + 1;
        }

        return counts;
    }

    /// <summary>
    /// Counts of the filtered set: the whole set feeds the message and the
    /// summary, the page keeps only its own records.
    /// </summary>
    private sealed class InventorySummary
    {
        private readonly Dictionary<string, CategoryTotal> _byCategory = new(StringComparer.Ordinal);
        private int _totalTypes;
        private int _inPlace;
        private int _nonParametric;
        private int _placed;
        private int _unplaced;
        private int _withoutTypes;
        private readonly List<string> _withoutTypeNames = new();

        internal int Total { get; set; }

        internal int CategoryCount => _byCategory.Count;

        internal void Add(
            string? name,
            string? category,
            int typeCount,
            bool isInPlace,
            bool isParametric,
            bool isPlaced)
        {
            var key = category ?? string.Empty;
            if (!_byCategory.TryGetValue(key, out var count))
                _byCategory.Add(key, count = new CategoryTotal());

            count.Families++;
            count.Types += typeCount;
            _totalTypes += typeCount;
            if (typeCount == 0)
            {
                _withoutTypes++;
                if (_withoutTypeNames.Count < 10 && !string.IsNullOrWhiteSpace(name))
                    _withoutTypeNames.Add(name!);
            }

            if (isInPlace)
                _inPlace++;
            if (!isParametric)
                _nonParametric++;
            if (isPlaced)
                _placed++;
            else
                _unplaced++;
        }

        internal Dictionary<string, object?> Build()
        {
            var rows = _byCategory
                .Select(pair => new Dictionary<string, object?>
                {
                    ["category"] = pair.Key.Length == 0 ? null : pair.Key,
                    ["families"] = pair.Value.Families,
                    ["types"] = pair.Value.Types
                })
                .OrderByDescending(row => (int)row["families"]!)
                .ThenBy(row => (string?)row["category"], StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new Dictionary<string, object?>
            {
                ["totalFamilies"] = Total,
                ["totalTypes"] = _totalTypes,
                ["byCategory"] = rows,
                ["inPlace"] = _inPlace,
                ["nonParametric"] = _nonParametric,
                ["placed"] = _placed,
                ["unplaced"] = _unplaced,
                ["familiesWithTypes"] = Total - _withoutTypes,
                ["familiesWithoutTypes"] = _withoutTypes,
                ["familiesWithoutTypesSample"] = _withoutTypeNames.Count > 0
                    ? _withoutTypeNames
                    : null,
                ["familyScanNote"] =
                    "Symbol-based scans (group_families_by_shared_parameters, " +
                    "find_families_with_parameter) see families that have types (FamilySymbol) " +
                    "or placed instances; familiesWithoutTypes are counted in totalFamilies " +
                    "but not in their familiesScanned."
            };
        }

        private sealed class CategoryTotal
        {
            internal int Families;

            internal int Types;
        }
    }
}
