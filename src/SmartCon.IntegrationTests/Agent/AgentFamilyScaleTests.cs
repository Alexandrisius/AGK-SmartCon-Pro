using System.Diagnostics;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Scale probe for the family editing pack: 200 parameters × 100 types, a live
/// read of all values and a 5000-value map. Thresholds are deliberately loose —
/// the test guards against quadratic behaviour, not against machine speed.
/// </summary>
public sealed class AgentFamilyScaleTests : RevitApiTest
{
    private const int TypeCount = 100;
    private const int SourceCount = 50;
    private const int TargetCount = 50;
    private const int ExtraCount = 100;

    private Document? _document;
    private Document? _familyDoc;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private string? _rfaPath;
    private long _familyId;

    private Document Doc => _document!;

    [Before(Test)]
    public void SeedLargeFamily()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
            Skip.Test("В установленном Revit нет подходящего .rft шаблона семейства");

        var directory = Path.Combine(Path.GetTempPath(), "SmartConFamilyTests");
        Directory.CreateDirectory(directory);
        _rfaPath = Path.Combine(directory, "SC_AgentFamilyScale.rfa");
        if (File.Exists(_rfaPath))
            File.Delete(_rfaPath);

        _familyDoc = Application.NewFamilyDocument(template);
        if (_familyDoc is null)
            Skip.Test("Revit не создал семейство из шаблона");

        try
        {
            using (var transaction = new Transaction(_familyDoc, "Seed large family"))
            {
                transaction.Start();
                var manager = _familyDoc.FamilyManager;

                var sources = new List<FamilyParameter>(SourceCount);
                for (var index = 0; index < SourceCount; index++)
                {
                    sources.Add(manager.AddParameter(
                        "S" + index.ToString("D3"),
                        GroupTypeId.General,
                        SpecTypeId.Length,
                        false));
                }

                var targets = new List<FamilyParameter>(TargetCount);
                for (var index = 0; index < TargetCount; index++)
                {
                    targets.Add(manager.AddParameter(
                        "T" + index.ToString("D3"),
                        GroupTypeId.General,
                        SpecTypeId.Length,
                        false));
                }

                for (var index = 0; index < ExtraCount; index++)
                {
                    manager.AddParameter(
                        "X" + index.ToString("D3"),
                        GroupTypeId.General,
                        SpecTypeId.Number,
                        false);
                }

                for (var typeIndex = 0; typeIndex < TypeCount; typeIndex++)
                {
                    var typeName = "T" + typeIndex.ToString("D3");
                    if (typeIndex == 0 && manager.CurrentType is not null)
                    {
                        manager.RenameCurrentType(typeName);
                    }
                    else
                    {
                        manager.NewType(typeName);
                    }

                    for (var sourceIndex = 0; sourceIndex < SourceCount; sourceIndex++)
                    {
                        manager.Set(
                            sources[sourceIndex],
                            (typeIndex * 10 + sourceIndex + 1) / 304.8);
                    }
                }

                transaction.Commit();
            }

            _familyDoc.SaveAs(_rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
            var family = _familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
            if (family is null)
                Skip.Test("Revit не загрузил большое семейство в проект");

            _familyDoc.Close(false);
            _familyDoc = null;
            _familyId = family.Id.GetValue();
        }
        catch (Exception ex)
        {
            Skip.Test("Не удалось создать большое семейство: " + ex.Message);
        }
    }

    [After(Test)]
    public void Close()
    {
        if (_familyDoc is not null && _familyDoc.IsValidObject)
        {
            try
            {
                _familyDoc.Close(false);
            }
            catch (Exception)
            {
                // Already closed.
            }
        }

        try
        {
            if (_rfaPath is not null && File.Exists(_rfaPath))
                File.Delete(_rfaPath);
        }
        catch (Exception)
        {
            // Temp cleanup is best-effort.
        }

        Doc.Close(false);
    }

    [Test]
    public async Task LargeFamily_ReadAndMap_StayWithinBudget()
    {
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

        var readTool = new GetFamilyParametersTool(new StubRevitContext(Doc));
        var readWatch = Stopwatch.StartNew();
        var read = readTool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + _familyId + ",\"limit\":500," +
                  "\"maxValuesPerParameter\":100}"),
            CancellationToken.None);
        readWatch.Stop();

        await Assert.That(read.Success).IsTrue();
        var readData = Data(read);
        await Assert.That(readData.GetProperty("total").GetInt32())
            .IsGreaterThanOrEqualTo(SourceCount + TargetCount + ExtraCount);
        await Assert.That(readData.GetProperty("typeCount").GetInt32()).IsEqualTo(TypeCount);

        var firstParameter = readData.GetProperty("parameters")[0];
        await Assert.That(firstParameter.GetProperty("valuesByType").GetArrayLength())
            .IsEqualTo(TypeCount);
        await Assert.That(readWatch.ElapsedMilliseconds).IsLessThan(60000);

        var metrics = "[scale] read 200 params x 100 types: " + readWatch.ElapsedMilliseconds +
                      " ms | ";

        var mapTool = new MapFamilyParametersTool(
            new StubRevitContext(Doc),
            _transactions!,
            _changeSets!);

        var mappings = new List<string>(SourceCount);
        for (var index = 0; index < SourceCount; index++)
        {
            mappings.Add("{\"source\":{\"name\":\"S" + index.ToString("D3") +
                         "\"},\"target\":{\"name\":\"T" + index.ToString("D3") + "\"}}");
        }

        var args = Parse(
            "{\"familyId\":" + _familyId + ",\"mappings\":[" +
            string.Join(",", mappings) + "],\"dryRun\":false}");

        var mapWatch = Stopwatch.StartNew();
        var map = mapTool.ExecuteForTests(args, approved: true, CancellationToken.None);
        mapWatch.Stop();

        await Assert.That(map.Success).IsTrue();
        var mapData = Data(map);
        await Assert.That(mapData.GetProperty("changed").GetArrayLength())
            .IsGreaterThanOrEqualTo(SourceCount * TypeCount);
        await Assert.That(mapData.GetProperty("readBack").GetProperty("verified").GetInt32())
            .IsGreaterThanOrEqualTo(SourceCount * TypeCount);
        await Assert.That(mapWatch.ElapsedMilliseconds).IsLessThan(180000);

        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        var growthMb = (memoryAfter - memoryBefore) / (1024.0 * 1024.0);
        metrics += "[scale] map " + (SourceCount * TypeCount) + " values: " +
                   mapWatch.ElapsedMilliseconds + " ms; payload " +
                   mapData.GetRawText().Length + " chars; managed heap growth " +
                   growthMb.ToString("F1") + " MB";
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "smartcon-family-scale.txt"),
                metrics);
        }
        catch (Exception)
        {
            // Metrics file is diagnostics only.
        }

        await Assert.That(growthMb).IsLessThan(256.0);
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");
}