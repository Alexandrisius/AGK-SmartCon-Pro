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
public sealed class AgentFamilyJobTests : RevitApiTest
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
}