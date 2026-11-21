using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Family batch job: one family per unit, backup + edit + load-back + close, and
/// per-unit rollback. The 100-family test is the A.7 acceptance bar.
/// </summary>
public sealed partial class AgentFamilyJobTests : RevitApiTest
{
    private const int FamilyCount = 100;
    private const int RollbackFamilyCount = 5;
    private const string FamilyPrefix = "SC_JobFam_";
    private const string MapSpec =
        "{\"select\":{\"nameContains\":\"SC_JobFam_\"},\"mappings\":[{\"source\":{\"name\":\"Ширина\"},\"target\":{\"name\":\"Высота\"}}]}";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private AgentDatabase? _database;
    private string? _databasePath;
    private SqliteAgentJobStore? _store;
    private JobRunner? _runner;
    private string? _sharedFilePrevious;
    private string? _sharedFilePath;
    private readonly List<FamilySeed> _seeds = new();

    private Document Doc => _document!;

    private string DocumentId => AgentDocumentIdentity.GetDocumentId(Doc);

    private SqliteAgentJobStore Store => _store!;

    private JobRunner Runner => _runner!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "sc-jobfam-" + Guid.NewGuid().ToString("N") + ".db");
        _database = new AgentDatabase(_databasePath);
        _database.Initialize();
        _store = new SqliteAgentJobStore(_database);
        _runner = new JobRunner(
            _store,
            new IJobAction[] { new MapFamilyParametersJobAction() },
            _transactions);
    }

    [After(Test)]
    public void Cleanup()
    {
        foreach (var seed in _seeds)
        {
            seed.Dispose();
        }

        _seeds.Clear();
        RestoreSharedParameterFile();
        Doc.Close(false);

        if (_databasePath is not null && File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Test]
    public async Task FamilyJob_100Families_CompletesAndClosesEveryDocument()
    {
        if (!SeedFamilies(FamilyCount))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Preview,
                SpecJson = MapSpec,
                Options = Options(batchSize: 10)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.Count).IsEqualTo(FamilyCount);
        await Assert.That(previewData.WillChangeCount).IsEqualTo(FamilyCount);

        var apply = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Apply,
                SpecJson = MapSpec,
                PreviewDigest = previewData.Digest,
                Options = Options(batchSize: 10)
            },
            Doc);

        await Assert.That(apply.Success).IsTrue();

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(10));

        await Assert.That(result.State).IsEqualTo(JobState.Completed);

        var items = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(items.Count).IsEqualTo(FamilyCount);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(FamilyCount);

        foreach (var seed in _seeds)
        {
            var width = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
            var height = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.HeightParam);
            await Assert.That(width).IsNotNull();
            await Assert.That(height).IsNotNull();
            await Assert.That(Math.Abs(height!.AsDouble() - width!.AsDouble()) < 1e-9).IsTrue();
        }

        // Every family document must be closed: only the project stays open.
        await Assert.That(Doc.Application.Documents.Cast<Document>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task FamilyJob_Rollback_RestoresValues()
    {
        if (!SeedFamilies(RollbackFamilyCount))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Preview,
                SpecJson = MapSpec,
                Options = Options(batchSize: 2)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var digest = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!.Digest;

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Apply,
                SpecJson = MapSpec,
                PreviewDigest = digest,
                Options = Options(batchSize: 2)
            },
            Doc);

        var applyResult = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));
        await Assert.That(applyResult.State).IsEqualTo(JobState.Completed);

        var rollback = Runner.Rollback(Doc, jobId, dryRun: false);

        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);
        await Assert.That(rollback.Restored).IsEqualTo(RollbackFamilyCount);
        await Assert.That(rollback.RestoreFailed).IsEqualTo(0);

        foreach (var seed in _seeds)
        {
            var width = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
            var height = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.HeightParam);
            await Assert.That(width).IsNotNull();
            await Assert.That(height).IsNotNull();
            await Assert.That(Math.Abs(height!.AsDouble() - width!.AsDouble()) < 1e-9).IsFalse();
        }

        await Assert.That(Doc.Application.Documents.Cast<Document>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task FamilyJob_SelectByNames_ResolvesAndWarns()
    {
        if (!SeedFamilies(3))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        // #433: two names resolve, the third matches nothing and must warn.
        var spec =
            "{\"select\":{\"familyNames\":[\"" + FamilyPrefix + "1\",\"" + FamilyPrefix + "2\"," +
            "\"НетТакогоСемейства\"]},\"mappings\":[{\"source\":{\"name\":\"Ширина\"}," +
            "\"target\":{\"name\":\"Высота\"}}]}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options(batchSize: 10)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.Count).IsEqualTo(2);
        await Assert.That(previewData.Warnings.Any(w => w.Contains("НетТакогоСемейства"))).IsTrue();

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Apply,
                SpecJson = spec,
                PreviewDigest = previewData.Digest,
                Options = Options(batchSize: 10)
            },
            Doc);

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));
        await Assert.That(result.State).IsEqualTo(JobState.Completed);

        var items = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(2);

        foreach (var seed in _seeds.Take(2))
        {
            var width = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
            var height = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.HeightParam);
            await Assert.That(width).IsNotNull();
            await Assert.That(height).IsNotNull();
            await Assert.That(Math.Abs(height!.AsDouble() - width!.AsDouble()) < 1e-9).IsTrue();
        }

        // The unselected family keeps its original values.
        var thirdWidth = _seeds[2].GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
        var thirdHeight = _seeds[2].GetProjectParameter(FamilySeed.Type1, FamilySeed.HeightParam);
        await Assert.That(Math.Abs(thirdHeight!.AsDouble() - thirdWidth!.AsDouble()) < 1e-9).IsFalse();
    }

    [Test]
    public async Task FamilyJob_CreateTarget_CreatesAndMaps()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFile();
        const string targetName = "ЦельНовая";
        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"mappings\":[" +
            "{\"source\":{\"name\":\"Ширина\"},\"target\":{\"name\":\"" + targetName +
            "\"},\"createTarget\":true}]}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options(batchSize: 1)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.Count).IsEqualTo(1);
        await Assert.That(previewData.WillChangeCount).IsEqualTo(1);

        var previewItems = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        // The JSON writer escapes Cyrillic, so the created-target name is asserted
        // through the parsed document instead of a raw substring.
        using var expected = System.Text.Json.JsonDocument.Parse(previewItems[0].ExpectedJson!);
        var creates = expected.RootElement.GetProperty("wouldCreateTargets");
        await Assert.That(creates.GetArrayLength()).IsEqualTo(1);
        await Assert.That(creates[0].GetProperty("name").GetString()).IsEqualTo(targetName);
        await Assert.That(creates[0].GetProperty("scope").GetString()).IsEqualTo("type");
        await Assert.That(previewItems[0].ActualJson!.Contains("target parameter will be created")).IsTrue();

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Apply,
                SpecJson = spec,
                PreviewDigest = previewData.Digest,
                Options = Options(batchSize: 1)
            },
            Doc);

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));
        await Assert.That(result.State).IsEqualTo(JobState.Completed);

        var applyItems = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(applyItems.Count(item => item.State == JobItemState.Done)).IsEqualTo(1);

        var seed = _seeds[0];
        var created = seed.GetProjectParameter(FamilySeed.Type1, targetName);
        var width = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
        await Assert.That(created).IsNotNull();
        await Assert.That(Math.Abs(created!.AsDouble() - width!.AsDouble()) < 1e-9).IsTrue();

        var rollback = Runner.Rollback(Doc, jobId, dryRun: false);

        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);
        await Assert.That(rollback.Restored).IsEqualTo(1);
        await Assert.That(seed.GetProjectParameter(FamilySeed.Type1, targetName)).IsNull();
    }

    [Test]
    public async Task FamilyJob_CreateTargetFalse_FailsPreviewWithHint()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"mappings\":[" +
            "{\"source\":{\"name\":\"Ширина\"},\"target\":{\"name\":\"НетТакойЦели\"}}]}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options(batchSize: 1)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.Count).IsEqualTo(1);
        await Assert.That(previewData.FailedCount).IsEqualTo(1);
        await Assert.That(previewData.Sample.Any(s => s.ErrorDetail!.Contains("createTarget"))).IsTrue();
    }

    [Test]
    public async Task FamilyJob_CreateTarget_NameCollisionInOtherScope_FailsPreview()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        // The family already has Примечание_экз (instance); the mapping asks for a
        // type-scoped target with the same name and createTarget=true — the apply
        // would die in AddParameter, so the preview must fail with the collision.
        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"mappings\":[" +
            "{\"source\":{\"name\":\"Ширина\"},\"target\":{\"name\":\"" +
            FamilySeed.InstanceParam + "\",\"scope\":\"type\"},\"createTarget\":true}]}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options(batchSize: 1)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.Count).IsEqualTo(1);
        await Assert.That(previewData.FailedCount).IsEqualTo(1);
        await Assert.That(previewData.WillChangeCount).IsEqualTo(0);
        var detail = previewData.Sample.First().ErrorDetail!;
        await Assert.That(detail.Contains("already exists")).IsTrue();
        await Assert.That(detail.Contains("instance")).IsTrue();
        await Assert.That(detail.Contains("createTarget")).IsTrue();
    }

    private bool SeedFamilies(int count)
    {
        for (var index = 1; index <= count; index++)
        {
            var seed = FamilySeed.Create(Application, Doc, FamilyPrefix + index, out var reason);
            if (seed is null)
            {
                if (reason is not null)
                {
                    Console.WriteLine("Family seed skipped: " + reason);
                }

                return false;
            }

            _seeds.Add(seed);
        }

        return true;
    }

    private static JobOptions Options(int batchSize) => new()
    {
        SavePolicy = JobSavePolicy.Never,
        BatchSize = batchSize,
        SliceBudgetMs = 1500,
        MaxItems = 500
    };

    /// <summary>Points Revit at a fresh shared parameter file for createTarget tests.</summary>
    private void UseSharedParameterFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SmartConFamilyTests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "jobfam-shared-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, string.Empty);
        _sharedFilePrevious = Application.SharedParametersFilename;
        _sharedFilePath = path;
        Application.SharedParametersFilename = path;
    }

    private void RestoreSharedParameterFile()
    {
        if (_sharedFilePath is null)
        {
            return;
        }

        try
        {
            Application.SharedParametersFilename = _sharedFilePrevious ?? string.Empty;
        }
        catch (Exception)
        {
            // Restoring the shared parameter file is best-effort in the test host.
        }

        try
        {
            if (File.Exists(_sharedFilePath))
            {
                File.Delete(_sharedFilePath);
            }
        }
        catch (IOException)
        {
        }

        _sharedFilePath = null;
    }
}