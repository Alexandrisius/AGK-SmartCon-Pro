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

    private static readonly string[] AvailableFields =
    {
        "familyId", "uniqueId", "name", "category", "categoryId",
        "typeCount", "placedInstanceCount", "isEditable", "isInPlace", "isParametric"
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
            "selects other record fields (placedInstanceCount tells placed-in-the-model " +
            "families from template-library ones: 0 = loaded but never placed), " +
            "compact=false returns all ten, summary=true adds counts over the whole " +
            "filtered set (totalFamilies, totalTypes, byCategory, placed, unplaced). " +
            "Families are not types and not instances: use list_family_types for the types " +
            "of a family. System families (walls, floors, pipes and the like) are not " +
            "listed here: their types come from list_system_types.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""},""nameContains"":{""type"":""string"",""description"":""Case-insensitive substring of the family name.""},""category"":{""anyOf"":[{""type"":""string""},{""type"":""array"",""items"":{""type"":""string""}}],""description"":""Category name or OST_* built-in name. A family matches any of the listed categories.""},""includeInPlace"":{""type"":""boolean"",""default"":true},""fields"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Record fields to return in compact mode. Allowed: familyId, uniqueId, name, category, categoryId, typeCount, placedInstanceCount, isEditable, isInPlace, isParametric. Default: familyId, name, category, typeCount. Unknown names return invalid_arguments.""},""compact"":{""type"":""boolean"",""default"":true,""description"":""true: each record carries only the fields listed in fields. false: every record carries all ten fields.""},""summary"":{""type"":""boolean"",""default"":false,""description"":""Adds summary over the whole filtered set, ignoring limit/offset: totalFamilies, totalTypes, byCategory [{category, families, types}] sorted by families desc, inPlace, nonParametric, placed, unplaced (families with/without placed instances).""},""limit"":{""type"":""integer"",""default"":200,""maximum"":1000},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var categories = ReadCategories(args);
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

        var families = new List<Dictionary<string, object?>>();
        var summary = new InventorySummary();
        var placedCounts = PlacedInstanceCounts(doc);
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

            if (!MatchesAnyCategory(family, categories))
                continue;

            var inWindow = ++summary.Total > offset && families.Count < limit;
            var categoryName = family.FamilyCategory?.Name;
            var typeCount = family.GetFamilySymbolIds().Count;
            placedCounts.TryGetValue(family.Id.GetValue(), out var placedInstanceCount);
            summary.Add(categoryName, typeCount, family.IsInPlace, family.IsParametric,
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
                ["isParametric"] = family.IsParametric
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

        var messageText = "Families: " + families.Count + " of " + summary.Total +
                          " (" + summary.CategoryCount + " categories).";
        if (page.Truncated)
        {
            messageText += AgentListPayload.TruncatedHint(
                withSummary ? TruncationWaysWithSummary : TruncationWays);
        }

        if (selection.IsDefault && compact && families.Count >= AgentListPayload.CompactHintThreshold)
            messageText += AgentListPayload.CompactFieldsHint(selection.Fields);

        return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
    }

    private static List<string> ReadCategories(JsonElement args)
    {
        var categories = new List<string>();
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("category", out var value))
            return categories;

        if (value.ValueKind == JsonValueKind.String)
            AddCategory(categories, value);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                AddCategory(categories, item);

        return categories;
    }

    private static void AddCategory(List<string> categories, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return;

        var text = value.GetString();
        if (!string.IsNullOrWhiteSpace(text))
            categories.Add(text!.Trim());
    }

    private static bool MatchesAnyCategory(RevitFamily family, List<string> categories)
    {
        foreach (var category in categories)
        {
            if (MatchesCategory(family, category))
                return true;
        }

        return categories.Count == 0;
    }

    private static bool MatchesCategory(RevitFamily family, string category)
    {
        var name = family.FamilyCategory?.Name;
        if (!string.IsNullOrEmpty(name)
            && string.Equals(name, category.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = category.Trim();
        var builtIn = trimmed.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : "OST_" + trimmed;
        var categoryId = family.FamilyCategory?.Id;
        if (categoryId is null)
            return false;

        return string.Equals(
            ((BuiltInCategory)categoryId.GetValue()).ToString(),
            builtIn,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One collector over the placed family instances: familyId -> instance
    /// count. Cheap (a single pass) and enough to tell the families the model
    /// really uses from the template-library ones that are only loaded.
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

        internal int Total { get; set; }

        internal int CategoryCount => _byCategory.Count;

        internal void Add(
            string? category, int typeCount, bool isInPlace, bool isParametric, bool isPlaced)
        {
            var key = category ?? string.Empty;
            if (!_byCategory.TryGetValue(key, out var count))
                _byCategory.Add(key, count = new CategoryTotal());

            count.Families++;
            count.Types += typeCount;
            _totalTypes += typeCount;
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
                ["unplaced"] = _unplaced
            };
        }

        private sealed class CategoryTotal
        {
            internal int Families;

            internal int Types;
        }
    }
}
