using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Seeding and measuring support of the list_families inventory tests: generic
/// families in several categories, the facts read straight from the document and
/// the payload size probe.
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    /// <summary>
    /// Loads <paramref name="count"/> generic model families named
    /// <see cref="InventoryFamilyPrefix"/>NNN with <paramref name="typesPerFamily"/> types
    /// each; the categories cycle through four built-in ones. One family document is
    /// renamed, saved and loaded per family. Returns the ids of the loaded families.
    /// </summary>
    private List<long> SeedGenericFamilies(int count, int typesPerFamily)
    {
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
            Skip.Test("В установленном Revit нет family-шаблона (.rft)");

        var number = NextInventoryNumber();
        var directory = Path.Combine(Path.GetTempPath(), "SmartConInventory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ids = new List<long>(count);
        var watch = Stopwatch.StartNew();
        Document? familyDoc = null;
        try
        {
            familyDoc = Application.NewFamilyDocument(template);
            var manager = familyDoc.FamilyManager;
            using (var transaction = new Transaction(familyDoc, "Seed inventory types"))
            {
                transaction.Start();
                if (manager.CurrentType is null)
                    manager.NewType("Тип1");
                else
                    manager.RenameCurrentType("Тип1");
                for (var index = 2; index <= typesPerFamily; index++)
                    manager.NewType("Тип" + index.ToString(CultureInfo.InvariantCulture));
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Транзакция сидирования типов не завершилась");
            }

            for (var index = 0; index < count; index++)
            {
                var name = InventoryFamilyPrefix + (number + index).ToString("D3", CultureInfo.InvariantCulture);
                using (var transaction = new Transaction(familyDoc, "Seed inventory family"))
                {
                    transaction.Start();
                    familyDoc.OwnerFamily.Name = name;
                    AssignInventoryCategory(familyDoc, index % 4);
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Транзакция сида семейства «" + name + "» не завершилась");
                }

                // SaveAs under the family's own name: the loaded family takes the name
                // of the saved file, so a rename without a save loads under the old name.
                familyDoc.SaveAs(
                    Path.Combine(directory, name + ".rfa"),
                    new SaveAsOptions { OverwriteExistingFile = true });
                var family = familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
                if (family is null || !string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Семейство «" + name + "» загрузилось как «" + family?.Name + "»");
                }

                ids.Add(family.Id.GetValue());
            }
        }
        finally
        {
            if (familyDoc is not null && familyDoc.IsValidObject)
            {
                try
                {
                    familyDoc.Close(false);
                }
                catch (Exception)
                {
                    // The document is already closed or invalid — nothing to do.
                }
            }

            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // Temp cleanup is best-effort.
            }

            watch.Stop();
            Probe(
                "PROBE list_families inventory seed: families=" + ids.Count +
                " ms=" + watch.ElapsedMilliseconds);
        }

        return ids;
    }

    private int NextInventoryNumber()
    {
        var next = 1;
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)))
        {
            if (element is not RevitFamily family || !family.Name.StartsWith(InventoryFamilyPrefix, StringComparison.Ordinal))
                continue;

            if (int.TryParse(
                    family.Name.Substring(InventoryFamilyPrefix.Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var loaded))
            {
                next = Math.Max(next, loaded + 1);
            }
        }

        return next;
    }

    private static void AssignInventoryCategory(Document familyDoc, int slot)
    {
        BuiltInCategory[] categories =
        [
            BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_Furniture,
            BuiltInCategory.OST_Casework,
            BuiltInCategory.OST_LightingFixtures
        ];
        var owner = familyDoc.OwnerFamily;
        var category = Category.GetCategory(familyDoc, categories[slot]);
        if (category is not null && owner.IsAppropriateCategoryId(category.Id))
            owner.FamilyCategory = category;
    }

    private InventoryFacts ReadInventoryFacts()
    {
        var facts = new InventoryFacts();
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)))
        {
            if (element is not RevitFamily family)
                continue;

            var id = family.Id.GetValue();
            var categoryName = family.FamilyCategory?.Name ?? string.Empty;
            var typeCount = family.GetFamilySymbolIds().Count;
            facts.Ids.Add(id);
            facts.TypeCount[id] = typeCount;
            facts.Names[id] = family.Name;
            facts.CategoryName[id] = categoryName;
            facts.BuiltIn[id] = family.FamilyCategory?.BuiltInCategory;
            if (!facts.ByCategory.TryGetValue(categoryName, out var row))
                facts.ByCategory.Add(categoryName, row = new int[2]);
            row[0]++;
            row[1] += typeCount;
            facts.Types += typeCount;
            if (family.IsInPlace)
                facts.InPlace++;
            if (!family.IsParametric)
                facts.NonParametric++;
        }

        return facts;
    }

    private static int CountSeeded(InventoryFacts facts, string category)
    {
        var count = 0;
        foreach (var pair in facts.CategoryName)
        {
            if (pair.Value == category && facts.Names[pair.Key].StartsWith(InventoryFamilyPrefix, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    private (int Records, int DataChars, int ResultChars, int FullChars) MeasureListFamilies(
        ListFamiliesTool tool,
        int limit)
    {
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"limit\":" + limit.ToString(CultureInfo.InvariantCulture) + "}"),
            CancellationToken.None);
        var data = Data(result);
        return (
            data.GetProperty("returned").GetInt32(),
            data.GetRawText().Length,
            ToolResultBudget.Serialize(result).Length,
            ToolResultBudget.SerializeFull(result).Length);
    }

    private static string Average((int Records, int DataChars, int ResultChars, int FullChars) page) =>
        page.Records == 0 ? "n/a" : (page.DataChars / page.Records).ToString(CultureInfo.InvariantCulture);

    private static List<long> FamilyIds(JsonElement data)
    {
        var ids = new List<long>();
        foreach (var record in data.GetProperty("families").EnumerateArray())
            ids.Add(record.GetProperty("familyId").GetInt64());
        return ids;
    }

    /// <summary>
    /// Writes to the real standard output: TUnit buffers the Console output of
    /// passing tests, so a plain Console.WriteLine never reaches the run log.
    /// </summary>
    private static void Probe(string line)
    {
        using var stdout = new StreamWriter(
            Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        stdout.WriteLine(line);
    }

    private static string NullToEmpty(string? value) => value ?? string.Empty;

    private static string Names(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? string.Join(",", element.EnumerateArray().Select(item => item.ToString()))
            : string.Join(",", element.EnumerateObject().Select(property => property.Name));

    private static string Names(string[] values) => string.Join(",", values);

    private sealed class InventoryFacts
    {
        internal List<long> Ids { get; } = new();

        internal Dictionary<long, int> TypeCount { get; } = new();

        internal Dictionary<long, string> CategoryName { get; } = new();

        internal Dictionary<long, string> Names { get; } = new();

        internal Dictionary<long, BuiltInCategory?> BuiltIn { get; } = new();

        internal Dictionary<string, int[]> ByCategory { get; } = new(StringComparer.Ordinal);

        internal int Total => Ids.Count;

        internal int Types;

        internal int InPlace;

        internal int NonParametric;
    }
}
