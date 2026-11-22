using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Inventory reads of list_families: compact records by default, field projection,
/// the category summary over the whole filtered set and the payload size.
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    private const string InventoryFamilyPrefix = "SC_Инвентарь_Изделие_";
    private const int InventoryMinimumSeeded = 48;
    private const int InventoryRecordBudget = 40000;
    private const int InventoryMeasuredRecords = 300;
    private const int InventoryEstimatedRecords = 500;

    private static readonly string[] NineFamilyFields =
    {
        "familyId", "uniqueId", "name", "category", "categoryId",
        "typeCount", "isEditable", "isInPlace", "isParametric"
    };

    private static readonly string[] CompactFamilyFields = { "familyId", "name", "category", "typeCount" };

    [Test]
    public async Task ListFamilies_Inventory_IsCompactSummarizedAndWithinBudget()
    {
        var target = InventoryTargetFamilies();
        var existing = new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)).GetElementCount();
        var seeded = SeedGenericFamilies(Math.Max(InventoryMinimumSeeded, target - existing), 2);
        var facts = ReadInventoryFacts();
        Probe(
            "PROBE list_families inventory: existing=" + existing + " seeded=" + seeded.Count +
            ", families=" + facts.Total + ", types=" + facts.Types +
            ", categories=" + facts.ByCategory.Count);
        if (facts.Total < InventoryMeasuredRecords)
        {
            Skip.Test("В документе меньше " + InventoryMeasuredRecords + " семейств (" + facts.Total +
                      ") — размер payload не измерить");
        }

        var tool = new ListFamiliesTool(new StubRevitContext(Doc));

        // (1) Default call: compact records with exactly the four default fields.
        var defaults = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);
        await Assert.That(defaults.Success).IsTrue();
        var defaultData = Data(defaults);
        var defaultCount = Math.Min(200, facts.Total);
        await Assert.That(defaultData.GetProperty("total").GetInt32()).IsEqualTo(facts.Total);
        await Assert.That(defaultData.GetProperty("returned").GetInt32()).IsEqualTo(defaultCount);
        await Assert.That(defaultData.GetProperty("truncated").GetBoolean()).IsEqualTo(facts.Total > 200);
        await Assert.That(Names(defaultData.GetProperty("availableFields"))).IsEqualTo(Names(NineFamilyFields));
        await Assert.That(Names(defaultData.GetProperty("fields"))).IsEqualTo(Names(CompactFamilyFields));
        foreach (var record in defaultData.GetProperty("families").EnumerateArray())
        {
            await Assert.That(Names(record)).IsEqualTo(Names(CompactFamilyFields));
            var id = record.GetProperty("familyId").GetInt64();
            await Assert.That(NullToEmpty(record.GetProperty("category").GetString()))
                .IsEqualTo(facts.CategoryName[id]);
            await Assert.That(record.GetProperty("typeCount").GetInt32()).IsEqualTo(facts.TypeCount[id]);
        }

        await Assert.That(defaults.Message!.StartsWith(
                "Families: " + defaultCount + " of " + facts.Total +
                " (" + facts.ByCategory.Count + " categories).",
                StringComparison.Ordinal))
            .IsTrue();
        await Assert.That(defaults.Message)
            .Contains("Compact records show familyId, name, category, typeCount; pass fields=[...]");
        await Assert.That(defaults.Message)
            .Contains("Narrow with category / nameContains, request fewer fields, or pass summary=true for counts.");

        // (2) summary=true counts the whole filtered set, ignoring limit/offset.
        var summarized = tool.ExecuteForTests(Doc, Parse("{\"limit\":5,\"summary\":true}"), CancellationToken.None);
        await Assert.That(summarized.Success).IsTrue();
        var summaryData = Data(summarized);
        await Assert.That(summaryData.GetProperty("returned").GetInt32()).IsEqualTo(5);
        await Assert.That(summaryData.GetProperty("families").GetArrayLength()).IsEqualTo(5);
        var summary = summaryData.GetProperty("summary");
        await Assert.That(summary.GetProperty("totalFamilies").GetInt32()).IsEqualTo(facts.Total);
        await Assert.That(summary.GetProperty("totalTypes").GetInt32()).IsEqualTo(facts.Types);
        await Assert.That(summary.GetProperty("inPlace").GetInt32()).IsEqualTo(facts.InPlace);
        await Assert.That(summary.GetProperty("nonParametric").GetInt32()).IsEqualTo(facts.NonParametric);
        var rows = summary.GetProperty("byCategory");
        await Assert.That(rows.GetArrayLength()).IsEqualTo(facts.ByCategory.Count);
        var previousFamilies = int.MaxValue;
        var previousCategory = string.Empty;
        foreach (var row in rows.EnumerateArray())
        {
            var category = row.GetProperty("category").ValueKind == JsonValueKind.Null
                ? string.Empty
                : row.GetProperty("category").GetString()!;
            var expected = facts.ByCategory[category];
            var families = row.GetProperty("families").GetInt32();
            await Assert.That(families).IsEqualTo(expected[0]);
            await Assert.That(row.GetProperty("types").GetInt32()).IsEqualTo(expected[1]);
            await Assert.That(families <= previousFamilies).IsTrue();
            if (families == previousFamilies)
            {
                await Assert.That(string.CompareOrdinal(
                    previousCategory.ToUpperInvariant(), category.ToUpperInvariant()) <= 0).IsTrue();
            }

            previousFamilies = families;
            previousCategory = category;
        }

        // (3) compact=false returns all nine fields; explicit fields replace the default set.
        var full = tool.ExecuteForTests(Doc, Parse("{\"compact\":false,\"limit\":5}"), CancellationToken.None);
        await Assert.That(full.Success).IsTrue();
        await Assert.That(Names(Data(full).GetProperty("fields"))).IsEqualTo(Names(NineFamilyFields));
        foreach (var record in Data(full).GetProperty("families").EnumerateArray())
            await Assert.That(Names(record)).IsEqualTo(Names(NineFamilyFields));

        var projected = tool.ExecuteForTests(
            Doc, Parse("{\"fields\":[\"name\",\"isEditable\"],\"limit\":5}"), CancellationToken.None);
        await Assert.That(projected.Success).IsTrue();
        await Assert.That(Names(Data(projected).GetProperty("fields"))).IsEqualTo("name,isEditable");
        foreach (var record in Data(projected).GetProperty("families").EnumerateArray())
            await Assert.That(Names(record)).IsEqualTo("name,isEditable");

        // (4) Payload size of compact records against the result budget.
        var measured = MeasureListFamilies(tool, InventoryMeasuredRecords);
        Probe(
            "PROBE list_families payload: measured records=" + measured.Records +
            " dataChars=" + measured.DataChars + " resultChars=" + measured.ResultChars +
            " budget=" + InventoryRecordBudget + " nameLength=" + InventoryFamilyNameLength() +
            " avgRecordChars=" + Average(measured));
        await Assert.That(measured.Records).IsEqualTo(InventoryMeasuredRecords);
        await Assert.That(measured.ResultChars).IsLessThan(InventoryRecordBudget);

        if (facts.Total >= InventoryEstimatedRecords)
        {
            var big = MeasureListFamilies(tool, InventoryEstimatedRecords);
            Probe(
                "PROBE list_families payload: measured records=" + big.Records +
                " dataChars=" + big.DataChars + " fullResultChars=" + big.FullChars +
                " budgetedResultChars=" + big.ResultChars +
                " fitsBudget=" + (big.FullChars <= InventoryRecordBudget ? "True" : "False") +
                " avgRecordChars=" + Average(big));
        }
        else
        {
            Probe(
                "PROBE list_families payload: estimated records=" + InventoryEstimatedRecords +
                " resultChars≈" + Estimate(measured, InventoryEstimatedRecords) +
                " fitsBudget=" + (Estimate(measured, InventoryEstimatedRecords) <= InventoryRecordBudget ? "True" : "False") +
                " (set SMARTCON_IT_FAMILY_INVENTORY_TARGET=" + InventoryEstimatedRecords +
                " to seed and measure it)");
        }
    }

    /// <summary>
    /// Families the inventory test seeds in total: 300 (the asserted page) unless
    /// SMARTCON_IT_FAMILY_INVENTORY_TARGET asks for more, e.g. 500 for a measured 500-record page.
    /// </summary>
    private static int InventoryTargetFamilies()
    {
        var raw = Environment.GetEnvironmentVariable("SMARTCON_IT_FAMILY_INVENTORY_TARGET");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target)
               && target >= InventoryMeasuredRecords
            ? target
            : InventoryMeasuredRecords;
    }

    private static int InventoryFamilyNameLength() =>
        InventoryFamilyPrefix.Length + 3;

    private static int Estimate(
        (int Records, int DataChars, int ResultChars, int FullChars) page,
        int records) =>
        page.Records == 0 ? 0 : page.FullChars + (records - page.Records) * (page.DataChars / page.Records);

    [Test]
    public async Task ListFamilies_CategoryArray_FiltersByAnyListedCategory()
    {
        SeedGenericFamilies(12, 2);
        var facts = ReadInventoryFacts();
        var furniture = facts.Ids.Where(id => facts.BuiltIn[id] == BuiltInCategory.OST_Furniture).ToList();
        var casework = facts.Ids.Where(id => facts.BuiltIn[id] == BuiltInCategory.OST_Casework).ToList();
        if (furniture.Count == 0 || casework.Count == 0)
        {
            Skip.Test(
                "Revit не назначил сид-семействам категории Furniture/Casework (" + furniture.Count + "/" +
                casework.Count + ") — фильтр category по массиву не проверить");
        }

        var tool = new ListFamiliesTool(new StubRevitContext(Doc));

        var both = tool.ExecuteForTests(
            Doc,
            Parse("{\"category\":[\"OST_Furniture\",\"OST_Casework\"],\"limit\":1000}"),
            CancellationToken.None);
        await Assert.That(both.Success).IsTrue();
        var bothIds = FamilyIds(Data(both));
        await Assert.That(bothIds.ToHashSet().SetEquals(furniture.Concat(casework))).IsTrue();
        await Assert.That(Data(both).GetProperty("total").GetInt32())
            .IsEqualTo(furniture.Count + casework.Count);
        await Assert.That(bothIds.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(both.Message!.StartsWith(
                "Families: " + bothIds.Count + " of " + bothIds.Count + " (2 categories).",
                StringComparison.Ordinal))
            .IsTrue();

        var furnitureOnly = tool.ExecuteForTests(
            Doc, Parse("{\"category\":\"Furniture\",\"limit\":1000}"), CancellationToken.None);
        await Assert.That(furnitureOnly.Success).IsTrue();
        await Assert.That(FamilyIds(Data(furnitureOnly)).ToHashSet().SetEquals(furniture)).IsTrue();

        var caseworkByName = tool.ExecuteForTests(
            Doc,
            Parse("{\"category\":\"" + facts.CategoryName[casework[0]] + "\",\"limit\":1000}"),
            CancellationToken.None);
        await Assert.That(caseworkByName.Success).IsTrue();
        await Assert.That(FamilyIds(Data(caseworkByName)).ToHashSet().SetEquals(casework)).IsTrue();
    }

    [Test]
    public async Task ListFamilies_FilteredSummary_CountsOnlyTheFilteredSet()
    {
        SeedGenericFamilies(12, 2);
        var facts = ReadInventoryFacts();

        var result = new ListFamiliesTool(new StubRevitContext(Doc)).ExecuteForTests(
            Doc,
            Parse("{\"nameContains\":\"sc_инвентарь_изделие_\",\"summary\":true,\"limit\":3}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(12);
        await Assert.That(data.GetProperty("returned").GetInt32()).IsEqualTo(3);
        var summary = data.GetProperty("summary");
        await Assert.That(summary.GetProperty("totalFamilies").GetInt32()).IsEqualTo(12);
        await Assert.That(summary.GetProperty("totalTypes").GetInt32()).IsEqualTo(24);
        var rows = summary.GetProperty("byCategory");
        await Assert.That(rows.GetArrayLength()).IsGreaterThanOrEqualTo(3);
        var rowTypes = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var seededInCategory = CountSeeded(facts, row.GetProperty("category").GetString()!);
            await Assert.That(row.GetProperty("families").GetInt32()).IsEqualTo(seededInCategory);
            await Assert.That(row.GetProperty("types").GetInt32()).IsEqualTo(2 * seededInCategory);
            rowTypes += row.GetProperty("types").GetInt32();
        }

        await Assert.That(rowTypes).IsEqualTo(24);
        await Assert.That(result.Message!.StartsWith("Families: 3 of 12 (", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Message)
            .Contains("Narrow with category / nameContains or request fewer fields.");
        await Assert.That(result.Message).DoesNotContain("pass summary=true");
        await Assert.That(result.Message).DoesNotContain("Compact records show");
    }

    [Test]
    public async Task ListFamilies_UnknownField_ReturnsInvalidArgumentsWithAllowedNames()
    {
        var result = new ListFamiliesTool(new StubRevitContext(Doc)).ExecuteForTests(
            Doc,
            Parse("{\"fields\":[\"name\",\"width\",\"Color\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
        await Assert.That(result.Message).IsNotNull();
        await Assert.That(result.Message!.StartsWith("Unknown field(s): width, Color.", StringComparison.Ordinal))
            .IsTrue();
        foreach (var field in NineFamilyFields)
            await Assert.That(result.Message.Contains(field, StringComparison.Ordinal)).IsTrue();
    }
}
