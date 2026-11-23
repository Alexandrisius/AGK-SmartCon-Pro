using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// What classify_types says and gives back when the budgets cut the selection.
/// </summary>
public sealed partial class AgentClassifyTypesTests
{
    [Test]
    public async Task Classify_MaxTypes_CutsEverything_ReportsTheCutFirstAndAddressesTheFamilies()
    {
        RequireWallTypes(2);
        await Assert.That(LoadSampleTable("classify-truncate-all.txt").Success).IsTrue();

        // A second type of the same wall family makes a family of two: it cannot
        // fit a budget of one type, so nothing is classified and the cut is the
        // whole story of the answer.
        var originalId = _wallTypeIds[0];
        var secondId = 0L;
        _transactions!.RunInTransaction(Doc, "Seed a second wall type", doc =>
        {
            secondId = ((WallType)doc.GetElement(ElementIdCompat.Create(originalId))!)
                .Duplicate("SC_Truncate_Second")
                .Id.GetValue();
        });
        var typeIds = new List<long> { originalId, secondId };

        var cut = await ClassifyAsync(new { typeIds, maxTypes = 1 });

        await Assert.That(cut.Success).IsTrue();
        var data = cut.Data!.Value;
        await Assert.That(data.GetProperty("selection").GetProperty("selected").GetInt32())
            .IsEqualTo(2);
        await Assert.That(data.GetProperty("selection").GetProperty("processed").GetInt32())
            .IsEqualTo(0);
        await Assert.That(data.GetProperty("selection").GetProperty("truncated").GetBoolean())
            .IsTrue();
        await Assert.That(data.GetProperty("notProcessed").GetProperty("families").GetInt32())
            .IsEqualTo(1);

        var message = cut.Message ?? string.Empty;
        await Assert.That(message.StartsWith("TRUNCATED", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message).DoesNotContain("0 of 0");

        // The families left out are addressed, not only named: a loaded family by
        // its id, a system family (walls have none) by the ids of its types.
        var notProcessed = data.GetProperty("notProcessed");
        await Assert.That(notProcessed.GetProperty("familyNames").EnumerateArray().Count())
            .IsEqualTo(1);
        await Assert.That(notProcessed.GetProperty("familyIds").EnumerateArray().Count())
            .IsEqualTo(0);
        await Assert.That(notProcessed.GetProperty("categories").EnumerateArray().Any()).IsTrue();
        var systemTypeIds = notProcessed.GetProperty("systemTypeIds").EnumerateArray()
            .Select(id => id.GetInt64())
            .ToList();
        await Assert.That(systemTypeIds.Count).IsEqualTo(2);
        foreach (var typeId in typeIds)
            await Assert.That(systemTypeIds.Contains(typeId)).IsTrue();

        // The next call takes exactly them.
        var retry = await ClassifyAsync(new { typeIds = systemTypeIds });
        await Assert.That(retry.Success).IsTrue();
        var retried = retry.Data!.Value.GetProperty("selection");
        await Assert.That(retried.GetProperty("selected").GetInt32()).IsEqualTo(2);
        await Assert.That(retried.GetProperty("processed").GetInt32()).IsEqualTo(2);
        await Assert.That(retried.GetProperty("truncated").GetBoolean()).IsFalse();
    }
}
