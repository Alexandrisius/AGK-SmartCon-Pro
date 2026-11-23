using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// A write addressed to an instance: the element shows a view of the parameter
/// its type owns, so the answer has to point at the type.
/// </summary>
public sealed partial class AgentClassificationToolsTests
{
    [Test]
    public async Task Set_ThroughAWallInstanceWithoutScope_PointsAtTheTypeInsteadOfRefusingTheElement()
    {
        RequireWallType();
        await Assert.That(Load(SampleFile("guard-instance-without-scope.txt"), dryRun: false).Success).IsTrue();
        var wallId = 0L;
        _transactions!.RunInTransaction(Doc, "Seed a wall", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            wallId = ModelSeed.TryCreateWall(doc, level.Id)?.Id.GetValue() ?? 0L;
        });
        if (wallId == 0L)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        // The instance carries an empty view of the Assembly Code its type stores.
        // Without a scope the write is neither impossible nor done: it belongs to
        // the type, and the answer has to say so.
        var withoutScope = Set("[{\"elementId\":" + wallId + ",\"parameter\":\"ASSEMBLY_CODE\",\"value\":\"20.10.10\"}]");

        await Assert.That(withoutScope.Success).IsFalse();
        await Assert.That(Failed(withoutScope).GetProperty("errorCode").GetString()).IsEqualTo("scope_mismatch");
        await Assert.That(Failed(withoutScope).GetProperty("reason").GetString()).Contains("scope=\"type\"");
        await Assert.That(CodeOf(WallTypeId) ?? string.Empty).IsEqualTo(string.Empty);

        var withScope = Set("[" + CodeItem(wallId, "20.10.10") + "]");
        await Assert.That(withScope.Success).IsTrue();
        await Assert.That(CodeOf(WallTypeId)).IsEqualTo("20.10.10");
    }
}
