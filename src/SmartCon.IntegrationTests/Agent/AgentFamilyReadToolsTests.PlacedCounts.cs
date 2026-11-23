using Autodesk.Revit.DB;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// placedInstanceCount / summary.placed / summary.unplaced of list_families
/// (C5-03): one collector over the placed instances counts the families that
/// have an instance of their OWN in the model - a family without one is not
/// "unused", it may be nested. The scan is a full pass over the model, so it
/// runs only for the calls that show the count. Partial of the test class:
/// reuses its seed and helpers.
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    private const string PlacedFieldRequest =
        "{\"fields\":[\"familyId\",\"name\",\"placedInstanceCount\"],\"summary\":true}";

    [Test]
    public async Task ListFamilies_PlacedInstanceCount_CountsTheInstancesOfTheFamilyItself()
    {
        var tool = new ListFamiliesTool(new StubRevitContext(Doc));

        // Before placing: the seeded family is loaded and has no instance of its own.
        var loaded = tool.ExecuteForTests(Doc, Parse(PlacedFieldRequest), CancellationToken.None);

        await Assert.That(loaded.Success).IsTrue();
        var loadedData = Data(loaded);
        var loadedFamily = FindFamily(loadedData, Seed.FamilyId);
        await Assert.That(loadedFamily.GetProperty("placedInstanceCount").GetInt32()).IsEqualTo(0);
        var loadedSummary = loadedData.GetProperty("summary");
        var total = loadedSummary.GetProperty("totalFamilies").GetInt32();
        await Assert.That(total).IsGreaterThan(0);
        await Assert.That(loadedSummary.GetProperty("placed").GetInt32()).IsEqualTo(0);
        await Assert.That(loadedSummary.GetProperty("unplaced").GetInt32()).IsEqualTo(total);

        // One placed instance: the family now has an instance of its own.
        var symbol = SeedSymbols().Values.First();
        using (var transaction = new Transaction(Doc, "Place an instance of the seed family"))
        {
            transaction.Start();
            PlaceWithNote(symbol, XYZ.Zero, "placed count probe");
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Транзакция размещения экземпляра не завершилась");
        }

        var placed = tool.ExecuteForTests(Doc, Parse(PlacedFieldRequest), CancellationToken.None);

        await Assert.That(placed.Success).IsTrue();
        var placedData = Data(placed);
        await Assert.That(FindFamily(placedData, Seed.FamilyId).GetProperty("placedInstanceCount").GetInt32())
            .IsEqualTo(1);
        var placedSummary = placedData.GetProperty("summary");
        await Assert.That(placedSummary.GetProperty("placed").GetInt32()).IsEqualTo(1);
        await Assert.That(placedSummary.GetProperty("unplaced").GetInt32())
            .IsEqualTo(placedSummary.GetProperty("totalFamilies").GetInt32() - 1);
        // Every family is on exactly one side of the line.
        await Assert.That(placedSummary.GetProperty("placed").GetInt32()
                          + placedSummary.GetProperty("unplaced").GetInt32())
            .IsEqualTo(placedSummary.GetProperty("totalFamilies").GetInt32());
    }

    [Test]
    public async Task ListFamilies_PlacedCount_IsComputedOnlyForTheCallsThatShowIt()
    {
        // The gate: the default compact call shows no count anywhere and must
        // not pay for the scan; the field, every field and the summary do.
        var defaults = new List<string> { "familyId", "name", "category", "typeCount" };
        await Assert.That(ListFamiliesTool.NeedsPlacedCounts(compact: true, withSummary: false, defaults)).IsFalse();
        await Assert.That(ListFamiliesTool.NeedsPlacedCounts(
            compact: true, withSummary: false, new List<string> { "name", "placedInstanceCount" })).IsTrue();
        await Assert.That(ListFamiliesTool.NeedsPlacedCounts(
            compact: true, withSummary: false, new List<string> { "name", "PLACEDINSTANCECOUNT" })).IsTrue();
        await Assert.That(ListFamiliesTool.NeedsPlacedCounts(
            compact: false, withSummary: false, new List<string> { "name" })).IsTrue();
        await Assert.That(ListFamiliesTool.NeedsPlacedCounts(
            compact: true, withSummary: true, defaults)).IsTrue();

        // The wiring follows the gate: no count in the default call, a real
        // count in the three calls that asked for one.
        var tool = new ListFamiliesTool(new StubRevitContext(Doc));
        var defaultCall = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);
        await Assert.That(defaultCall.Success).IsTrue();
        await Assert.That(FindFamily(Data(defaultCall), Seed.FamilyId).TryGetProperty("placedInstanceCount", out _))
            .IsFalse();
        await Assert.That(Data(defaultCall).TryGetProperty("summary", out _)).IsFalse();

        var byField = tool.ExecuteForTests(
            Doc, Parse("{\"fields\":[\"familyId\",\"name\",\"placedInstanceCount\"]}"), CancellationToken.None);
        await Assert.That(byField.Success).IsTrue();
        await Assert.That(FindFamily(Data(byField), Seed.FamilyId).GetProperty("placedInstanceCount").GetInt32())
            .IsEqualTo(0);

        var full = tool.ExecuteForTests(Doc, Parse("{\"compact\":false}"), CancellationToken.None);
        await Assert.That(full.Success).IsTrue();
        await Assert.That(FindFamily(Data(full), Seed.FamilyId).GetProperty("placedInstanceCount").GetInt32())
            .IsEqualTo(0);

        var summarized = tool.ExecuteForTests(Doc, Parse("{\"summary\":true}"), CancellationToken.None);
        await Assert.That(summarized.Success).IsTrue();
        var summary = Data(summarized).GetProperty("summary");
        await Assert.That(summary.GetProperty("placed").GetInt32()
                          + summary.GetProperty("unplaced").GetInt32())
            .IsEqualTo(summary.GetProperty("totalFamilies").GetInt32());
        await Assert.That(FindFamily(Data(summarized), Seed.FamilyId).TryGetProperty("placedInstanceCount", out _))
            .IsFalse();
    }
}
