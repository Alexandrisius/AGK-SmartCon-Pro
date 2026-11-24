using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Nice3point.TUnit.Revit;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Parameter-audit reads of the family pack (S.33/S.35): prefix matching,
/// compact rows, the honest match total and matchedCount.
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    [Test]
    public async Task FindFamiliesWithParameter_CompactRow_CarriesNameCountAndSample()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"Ширина\",\"match\":\"exact\",\"compact\":true}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var family = FindFamily(Data(result), Seed.FamilyId);
        await Assert.That(family.GetProperty("familyName").GetString())
            .IsEqualTo(Seed.FamilyName);
        await Assert.That(family.GetProperty("matchedParameterCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        var sample = family.GetProperty("sample");
        await Assert.That(sample.GetArrayLength()).IsGreaterThanOrEqualTo(1);
        await Assert.That(sample[0].GetString()).IsEqualTo("Ширина");
    }

    [Test]
    public async Task FindFamiliesWithParameter_PrefixMatch_FindsLocalizedPrefix()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterNamePrefix\":\"Шир\",\"compact\":true}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("match").GetString()).IsEqualTo("prefix");
        await Assert.That(ContainsFamily(data, Seed.FamilyId)).IsTrue();
    }

    [Test]
    public async Task FindFamiliesWithParameter_FieldsProjection_ReturnsOnlyRequestedKeys()
    {
        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"Ширина\",\"compact\":true," +
                  "\"fields\":[\"familyId\",\"familyName\",\"matchedParameterCount\",\"sample\"]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var family = FindFamily(Data(result), Seed.FamilyId);
        var keys = family.EnumerateObject().Select(property => property.Name).ToList();
        await Assert.That(keys.Count).IsEqualTo(4);
        await Assert.That(keys.Contains("familyId")).IsTrue();
        await Assert.That(keys.Contains("familyName")).IsTrue();
        await Assert.That(keys.Contains("matchedParameterCount")).IsTrue();
        await Assert.That(keys.Contains("sample")).IsTrue();
        await Assert.That(keys.Contains("matchedTypes")).IsFalse();
    }

    [Test]
    public async Task FindFamiliesWithParameter_InstancePass_DoesNotListUnmatchedFamilies()
    {
        var family = new FilteredElementCollector(Doc)
            .OfClass(typeof(RevitFamily))
            .Cast<RevitFamily>()
            .First(item => string.Equals(
                item.Name, Seed.FamilyName, StringComparison.OrdinalIgnoreCase));
        var symbol = (FamilySymbol)Doc.GetElement(family.GetFamilySymbolIds().First());

        using (var transaction = new Transaction(Doc, "Place instance"))
        {
            transaction.Start();
            if (!symbol.IsActive)
                symbol.Activate();

            Doc.Create.NewFamilyInstance(XYZ.Zero, symbol, StructuralType.NonStructural);
            transaction.Commit();
        }

        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        // The instance pass used to add every family with a placed instance to
        // families[], so a search that matched nothing reported total>0.
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"НетТакогоПараметра\",\"includeInstanceParameters\":true}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("families").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task GetFamilyParameters_NameFilter_ReportsMatchedCount()
    {
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));

        var matched = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId +
                  ",\"nameContains\":\"Шир\",\"includeValues\":false}"),
            CancellationToken.None);

        await Assert.That(matched.Success).IsTrue();
        var matchedData = Data(matched);
        await Assert.That(matchedData.GetProperty("matchedCount").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(matchedData.GetProperty("total").GetInt32())
            .IsEqualTo(matchedData.GetProperty("matchedCount").GetInt32());

        var empty = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId +
                  ",\"nameContains\":\"НетТакогоПараметра\",\"includeValues\":false}"),
            CancellationToken.None);

        await Assert.That(empty.Success).IsTrue();
        var emptyData = Data(empty);
        await Assert.That(emptyData.GetProperty("matchedCount").GetInt32()).IsEqualTo(0);
        await Assert.That(emptyData.GetProperty("parameters").GetArrayLength()).IsEqualTo(0);
    }
}
