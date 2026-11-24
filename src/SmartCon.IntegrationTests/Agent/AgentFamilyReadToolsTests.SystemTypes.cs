using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Direct typeIds of SYSTEM family types (WallType, …) in list_family_types:
/// classify_types hands out exactly these ids, and the tool used to answer
/// "type_not_found / object is absent" for types that clearly exist (live
/// case 5, round 11).
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    [Test]
    public async Task ListFamilyTypes_SystemTypeIds_ReturnSystemTypeRows()
    {
        var wallTypes = new FilteredElementCollector(Doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .Take(2)
            .ToList();
        if (wallTypes.Count < 2)
            Skip.Test("В шаблоне меньше двух типов стен");

        var firstId = wallTypes[0].Id.GetValue();
        var expectedCategory = wallTypes[0].Category?.Name;
        var markParameter = wallTypes[0].get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
        var markName = markParameter?.Definition?.Name ?? "Марка типа";
        using (var transaction = new Transaction(Doc, "Set wall type mark"))
        {
            transaction.Start();
            markParameter?.Set("SC-SYS-1");
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Транзакция марки типа стены не завершилась");
        }

        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"typeIds\":[" + firstId + "," + wallTypes[1].Id.GetValue() +
                  "],\"parameterNames\":[\"" + markName + "\"],\"fields\":[\"isPlaced\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        var systemTypes = data.GetProperty("systemTypes");
        await Assert.That(systemTypes.GetArrayLength()).IsEqualTo(2);
        await Assert.That(data.GetProperty("systemTypeCount").GetInt32()).IsEqualTo(2);

        var first = systemTypes[0];
        await Assert.That(first.GetProperty("typeId").GetInt64()).IsEqualTo(firstId);
        await Assert.That(first.GetProperty("familyKind").GetString()).IsEqualTo("system");
        await Assert.That(string.IsNullOrEmpty(first.GetProperty("name").GetString())).IsFalse();
        // Revit gives system types an honest family name (Basic Wall): the row
        // must not pretend the type has no family at all.
        await Assert.That(string.IsNullOrEmpty(first.GetProperty("familyName").GetString()))
            .IsFalse();
        await Assert.That(first.GetProperty("category").GetString()).IsEqualTo(expectedCategory);
        await Assert.That(first.TryGetProperty("values", out var values)).IsTrue();
        await Assert.That(values.GetProperty(markName).GetString()).IsEqualTo("SC-SYS-1");
        // Family types stayed untouched: the rows went to systemTypes[], not types[].
        await Assert.That(data.GetProperty("types").GetArrayLength()).IsEqualTo(0);

        // The message names the system rows instead of claiming absence.
        await Assert.That(result.Message).Contains("2 system family type(s)");
        await Assert.That(result.Message).DoesNotContain("type_not_found");
    }

    [Test]
    public async Task ListFamilyTypes_MixedSystemAndUnknownIds_FailsHonestly()
    {
        var wallType = new FilteredElementCollector(Doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault();
        if (wallType is null)
            Skip.Test("В шаблоне нет типов стен");

        Element wall;
        using (var transaction = new Transaction(Doc, "Seed wall for typeIds test"))
        {
            transaction.Start();
            var level = Level.Create(Doc, 3.0);
            var line = Line.CreateBound(new XYZ(0, 0, 0), new XYZ(10, 0, 0));
            wall = Wall.Create(Doc, line, wallType.Id, level.Id, 3.0, 0.0, false, false);
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Транзакция сида стены не завершилась");
        }

        var tool = new ListFamilyTypesTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"typeIds\":[" + wallType.Id.GetValue() + ",999999999," +
                  wall.Id.GetValue() + "]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("type_not_found");
        var data = Data(result);
        // Only the truly absent id is "missing"; the wall instance is an element,
        // not a type - a different honest reason, never "object is absent".
        await Assert.That(Names(data.GetProperty("missing"))).IsEqualTo("999999999");
        await Assert.That(Names(data.GetProperty("notTypes")))
            .IsEqualTo(wall.Id.GetValue().ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(result.Message).Contains("list_system_types");
        await Assert.That(result.Message).DoesNotContain("re-read the family with list_family_types");
    }
}
