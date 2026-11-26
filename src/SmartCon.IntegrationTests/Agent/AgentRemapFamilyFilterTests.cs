using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using Autodesk.Revit.DB.Structure;
using Nice3point.TUnit.Revit;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// S.53: remap_parameter_values filtered by family (familyNames/familyIds — the
/// stable key for placed family instances) and paging with offset, plus the
/// unresolved-family contract. This is the project-side path for INSTANCE
/// parameter values of placed elements after a family migration.
/// </summary>
public sealed class AgentRemapFamilyFilterTests : RevitApiTest
{
    private const string SourceParameter = "Примечание_экз";
    private const string TargetParameter = "Примечание_цель";

    private Document? _document;
    private FamilySeed? _seed;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;

    // Element ids as longs: a List<ElementId> field initializer touches the
    // Revit assembly during discovery (before the injector runs) and kills
    // the whole session with "Attempted to write protected memory"
    // (Nice3point revit-testing rule: no Revit API at construction).
    private readonly List<long> _instanceIds = new();

    private Document Doc => _document!;

    private FamilySeed Seed => _seed!;

    [Before(Test)]
    public void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _seed = FamilySeed.Create(Application, Doc, "SC_AgentRemapFilter", out var reason);
        if (_seed is null)
        {
            Skip.Test(reason ?? "Не удалось сидировать семейство");
            return;
        }

        var addTarget = new AddFamilyParameterTool(
                new StubRevitContext(Doc), _transactions, _changeSets)
            .ExecuteForTests(
                Parse("{\"familyId\":" + Seed.FamilyId + ",\"name\":\"" + TargetParameter + "\"," +
                      "\"storageType\":\"text\",\"isInstance\":true,\"dryRun\":false}"),
                approved: true,
                CancellationToken.None);
        if (!addTarget.Success)
            Skip.Test("Не удалось добавить целевой параметр: " + addTarget.Message);

        // Three placed instances; source values on the first two, the third
        // stays empty (it must read as skipped, not remapped).
        var family = new FilteredElementCollector(Doc)
            .OfClass(typeof(RevitFamily))
            .Cast<RevitFamily>()
            .First(item => string.Equals(
                item.Name, Seed.FamilyName, StringComparison.OrdinalIgnoreCase));
        var symbol = (FamilySymbol)Doc.GetElement(family.GetFamilySymbolIds().First());
        using (var transaction = new Transaction(Doc, "Place instances"))
        {
            transaction.Start();
            if (!symbol.IsActive)
                symbol.Activate();
            for (var index = 0; index < 3; index++)
            {
                var instance = Doc.Create.NewFamilyInstance(
                    new XYZ(index * 2.0, 0, 0), symbol, StructuralType.NonStructural);
                _instanceIds.Add(instance.Id.GetValue());
            }

            for (var index = 0; index < 2; index++)
            {
                Doc.GetElement(ElementIdCompat.Create(_instanceIds[index]))
                    .LookupParameter(SourceParameter)?.Set("Текст" + (index + 1));
            }

            transaction.Commit();
        }

        // Seeded source values must be visible right away (diagnostic guard).
        for (var index = 0; index < 2; index++)
        {
            var seeded = Doc.GetElement(ElementIdCompat.Create(_instanceIds[index]))
                .LookupParameter(SourceParameter)?.AsString();
            if (seeded != "Текст" + (index + 1))
            {
                Skip.Test("Источник не засеялся: instance " + index + " value='" +
                          seeded + "' — параметр не найден или Set не применился");
                return;
            }
        }
    }

    [After(Test)]
    public void Close()
    {
        _seed?.Dispose();
        Doc.Close(false);
    }

    [Test]
    public async Task Remap_FamilyNames_CopiesOnlyThatFamilyInstances()
    {
        var result = Run(
            "{\"mapping\":[{\"source\":{\"name\":\"" + SourceParameter + "\"}," +
            "\"target\":{\"name\":\"" + TargetParameter + "\"}}]," +
            "\"filter\":{\"familyNames\":[\"" + Seed.FamilyName + "\"]},\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(2);
        await Assert.That(data.GetProperty("skipped").GetArrayLength()).IsEqualTo(1);
        await Assert.That(TargetOf(0)).IsEqualTo("Текст1");
        await Assert.That(TargetOf(1)).IsEqualTo("Текст2");
        // The empty-source instance keeps its empty target ("" for text).
        await Assert.That(string.IsNullOrEmpty(TargetOf(2))).IsTrue();
    }

    [Test]
    public async Task Remap_OffsetPagesUntilHasMoreDisappears()
    {
        var first = Run(
            "{\"mapping\":[{\"source\":{\"name\":\"" + SourceParameter + "\"}," +
            "\"target\":{\"name\":\"" + TargetParameter + "\"}}]," +
            "\"filter\":{\"familyNames\":[\"" + Seed.FamilyName + "\"],\"limit\":1}," +
            "\"offset\":0,\"dryRun\":false}");

        await Assert.That(first.Success).IsTrue();
        var firstData = Data(first);
        await Assert.That(firstData.GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(firstData.GetProperty("hasMore").GetBoolean()).IsTrue();
        await Assert.That(firstData.GetProperty("nextOffset").GetInt32()).IsEqualTo(1);
        var filledAfterFirst = CountFilledTargets();
        await Assert.That(filledAfterFirst).IsEqualTo(1);

        var second = Run(
            "{\"mapping\":[{\"source\":{\"name\":\"" + SourceParameter + "\"}," +
            "\"target\":{\"name\":\"" + TargetParameter + "\"}}]," +
            "\"filter\":{\"familyNames\":[\"" + Seed.FamilyName + "\"],\"limit\":1}," +
            "\"offset\":1,\"dryRun\":false}");

        await Assert.That(second.Success).IsTrue();
        var secondData = Data(second);
        await Assert.That(secondData.GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(CountFilledTargets()).IsEqualTo(2);
        // The third instance matches the filter but has no source value: the
        // probe still reports it, so the honest page loop ends on it.
        await Assert.That(secondData.GetProperty("hasMore").GetBoolean()).IsTrue();
        await Assert.That(secondData.GetProperty("nextOffset").GetInt32()).IsEqualTo(2);

        var third = Run(
            "{\"mapping\":[{\"source\":{\"name\":\"" + SourceParameter + "\"}," +
            "\"target\":{\"name\":\"" + TargetParameter + "\"}}]," +
            "\"filter\":{\"familyNames\":[\"" + Seed.FamilyName + "\"],\"limit\":1}," +
            "\"offset\":2,\"dryRun\":false}");

        await Assert.That(third.Success).IsTrue();
        await Assert.That(Data(third).GetProperty("changed").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Data(third).TryGetProperty("hasMore", out _)).IsFalse();
    }

    [Test]
    public async Task Remap_UnresolvedFamilyName_FailsWithFamilyNotFound()
    {
        var result = Run(
            "{\"mapping\":[{\"source\":{\"name\":\"" + SourceParameter + "\"}," +
            "\"target\":{\"name\":\"" + TargetParameter + "\"}}]," +
            "\"filter\":{\"familyNames\":[\"SC_NoSuchFamily\"]},\"dryRun\":true}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("family_not_found");
        await Assert.That(result.Message!).Contains("list_families");
    }

    private ToolResult Run(string json) =>
        new RemapParameterValuesTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(Parse(json), approved: true, CancellationToken.None);

    private string? TargetOf(int index) =>
        Doc.GetElement(ElementIdCompat.Create(_instanceIds[index]))
            .LookupParameter(TargetParameter)?.AsString();

    private int CountFilledTargets()
    {
        var filled = 0;
        foreach (var id in _instanceIds)
        {
            if (!string.IsNullOrEmpty(
                    Doc.GetElement(ElementIdCompat.Create(id))
                        .LookupParameter(TargetParameter)?.AsString()))
            {
                filled++;
            }
        }

        return filled;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");
}
