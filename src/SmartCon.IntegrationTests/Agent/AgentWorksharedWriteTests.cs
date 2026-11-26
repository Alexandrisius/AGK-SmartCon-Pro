using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Writing into a workshared local copy: the blanket workshared_not_supported
/// refusal is gone, the batch takes ownership before the transaction and a
/// detached copy is edited like a plain document.
/// </summary>
public sealed class AgentWorksharedWriteTests : RevitApiTest
{
    private string? _directory;
    private string? _centralPath;
    private string? _wallUniqueId;

    [Before(Test)]
    public void SeedCentral()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "SmartConWorksharing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _centralPath = Path.Combine(_directory, "SC_Workshared_Central.rvt");

        var seed = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            if (!seed.CanEnableWorksharing())
                Skip.Test("Revit не разрешает включить worksharing в новом проекте");

            using (var transaction = new Transaction(seed, "Seed workshared wall"))
            {
                transaction.Start();
                var level = ModelSeed.CreateLevel(seed, 3.0);
                var wall = ModelSeed.TryCreateWall(seed, level.Id);
                if (wall is null)
                    Skip.Test("В шаблоне проекта нет стены/типа стены");
                _wallUniqueId = wall.UniqueId;
                transaction.Commit();
            }

            try
            {
                seed.EnableWorksharing("Shared Levels and Grids", "Workset1");
                var options = new SaveAsOptions { OverwriteExistingFile = true };
                options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                seed.SaveAs(_centralPath, options);
            }
            catch (Exception ex)
            {
                Skip.Test("Revit не сохранил центральную модель: " + ex.Message);
            }
        }
        finally
        {
            seed.Close(false);
        }
    }

    [After(Test)]
    public void Cleanup()
    {
        try
        {
            if (_directory is not null && Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp file is not a test failure.
        }
    }

    [Test]
    public async Task SetParameters_LocalCopy_WritesAndReportsWorksharing()
    {
        var localPath = Path.Combine(_directory!, "SC_Workshared_Local.rvt");
        WorksharingUtils.CreateNewLocal(
            ModelPathUtils.ConvertUserVisiblePathToModelPath(_centralPath!),
            ModelPathUtils.ConvertUserVisiblePathToModelPath(localPath));
        var doc = Application.OpenDocumentFile(localPath);
        try
        {
            if (!doc.IsWorkshared || doc.IsDetached)
                Skip.Test("Локальная копия открылась не как workshared local");

            var wallId = FindWallId(doc);
            var result = WriteMark(doc, wallId, "WS_After");

            await Assert.That(result.Success).IsTrue();
            var data = result.Data!.Value;
            await Assert.That(data.GetProperty("worksharing").GetProperty("mode").GetString())
                .IsEqualTo("local_copy");
            await Assert.That(data.GetProperty("worksharing").GetProperty("checkedOut").GetInt32())
                .IsGreaterThanOrEqualTo(1);
            await Assert.That(WallMark(doc, wallId)).IsEqualTo("WS_After");
        }
        finally
        {
            doc.Close(false);
        }
    }

    [Test]
    public async Task SetParameters_DetachedCopy_WritesWithoutOwnership()
    {
        var options = new OpenOptions
        {
            DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets
        };
        var doc = Application.OpenDocumentFile(
            ModelPathUtils.ConvertUserVisiblePathToModelPath(_centralPath!), options);
        try
        {
            if (!doc.IsWorkshared || !doc.IsDetached)
                Skip.Test("Модель открылась не как detached workshared copy");

            var wallId = FindWallId(doc);
            var result = WriteMark(doc, wallId, "WS_Detached");

            await Assert.That(result.Success).IsTrue();
            await Assert.That(result.Data!.Value.GetProperty("worksharing")
                    .GetProperty("mode").GetString())
                .IsEqualTo("detached");
            await Assert.That(WallMark(doc, wallId)).IsEqualTo("WS_Detached");
        }
        finally
        {
            doc.Close(false);
        }
    }

    private ToolResult WriteMark(Document doc, long wallId, string value)
    {
        var tool = new SetParametersTool(
            new StubRevitContext(doc),
            new RevitTransactionService(new StubRevitContext(doc)),
            new InMemoryAgentChangeSetStore());
        return tool.ExecuteForTests(
            Parse("{\"items\":[{\"elementId\":" + wallId +
                  ",\"parameter\":\"Mark\",\"value\":\"" + value + "\"}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
    }

    private long FindWallId(Document doc)
    {
        foreach (var element in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_Walls)
                     .WhereElementIsNotElementType())
        {
            if (string.Equals(element.UniqueId, _wallUniqueId, StringComparison.Ordinal))
                return element.Id.GetValue();
        }

        throw new InvalidOperationException("Seeded wall is missing in the workshared copy");
    }

    private static string? WallMark(Document doc, long wallId)
    {
        var wall = doc.GetElement(ElementIdCompat.Create(wallId));
        return wall?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
