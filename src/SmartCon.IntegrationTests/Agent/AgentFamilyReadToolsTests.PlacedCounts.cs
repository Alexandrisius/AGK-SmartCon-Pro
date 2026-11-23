using Autodesk.Revit.DB;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// placedInstanceCount / summary.placed / summary.unplaced of list_families
/// (C5-03): one collector over the placed instances separates the families the
/// model really uses from the template-library ones that are only loaded.
/// Partial of the test class, reuses its seed and helpers.
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    [Test]
    public async Task ListFamilies_PlacedInstanceCount_SeparatesModelFromTemplateLibrary()
    {
        var tool = new ListFamiliesTool(new StubRevitContext(Doc));

        // Before placing: the seeded family is loaded but never placed.
        var loaded = tool.ExecuteForTests(
            Doc,
            Parse("{\"fields\":[\"familyId\",\"name\",\"placedInstanceCount\"],\"summary\":true}"),
            CancellationToken.None);

        await Assert.That(loaded.Success).IsTrue();
        var loadedData = Data(loaded);
        var loadedFamily = FindFamily(loadedData, Seed.FamilyId);
        await Assert.That(loadedFamily.GetProperty("placedInstanceCount").GetInt32()).IsEqualTo(0);
        var loadedSummary = loadedData.GetProperty("summary");
        var total = loadedSummary.GetProperty("totalFamilies").GetInt32();
        await Assert.That(total).IsGreaterThan(0);
        await Assert.That(loadedSummary.GetProperty("placed").GetInt32()).IsEqualTo(0);
        await Assert.That(loadedSummary.GetProperty("unplaced").GetInt32()).IsEqualTo(total);

        // One placed instance turns the family into a model family.
        var symbol = SeedSymbols().Values.First();
        using (var transaction = new Transaction(Doc, "place seed instance"))
        {
            transaction.Start();
            PlaceWithNote(symbol, XYZ.Zero, "inventory probe");
            transaction.Commit();
        }

        var placed = tool.ExecuteForTests(
            Doc,
            Parse("{\"fields\":[\"familyId\",\"name\",\"placedInstanceCount\"],\"summary\":true}"),
            CancellationToken.None);

        await Assert.That(placed.Success).IsTrue();
        var placedData = Data(placed);
        var placedFamily = FindFamily(placedData, Seed.FamilyId);
        await Assert.That(placedFamily.GetProperty("placedInstanceCount").GetInt32()).IsEqualTo(1);
        var placedSummary = placedData.GetProperty("summary");
        await Assert.That(placedSummary.GetProperty("placed").GetInt32()).IsEqualTo(1);
        await Assert.That(placedSummary.GetProperty("unplaced").GetInt32())
            .IsEqualTo(placedSummary.GetProperty("totalFamilies").GetInt32() - 1);
    }
}
