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
/// Family batch jobs of the ФОП migration write path (#536): bulk parameter
/// adds with an authored formula (add_family_parameter action) and the native
/// type↔instance scope switch (set_family_parameter_scope action) — preview,
/// apply, honest skips on the second run and backup-based rollback.
/// </summary>
public sealed class AgentFamilyAddScopeJobTests : RevitApiTest
{
    private const int FamilyCount = 3;
    private const string FamilyPrefix = "SC_JobAdd_";
    private const string CountDuplicate = "Количество_Дубль";
    private const string LengthDuplicate = "Длина_Дубль";

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
            "sc-jobadd-" + Guid.NewGuid().ToString("N") + ".db");
        _database = new AgentDatabase(_databasePath);
        _database.Initialize();
        _store = new SqliteAgentJobStore(_database);
        _runner = new JobRunner(
            _store,
            new IJobAction[] { new AddFamilyParametersJobAction(), new SetFamilyParameterScopeJobAction() },
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
            catch (Exception)
            {
                // A locked temp database must not fail the test run.
            }
        }

    }

    [Test]
    public async Task AddJob_AddsParametersWithFormula_ThenSkipsAndRollsBack()
    {
        if (!SeedFamilies())
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        var spec = AddSpec();
        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "add_family_parameter",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options()
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

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "add_family_parameter",
                Mode = JobMode.Apply,
                SpecJson = spec,
                PreviewDigest = previewData.Digest,
                Options = Options()
            },
            Doc);

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));
        await Assert.That(result.State).IsEqualTo(JobState.Completed);

        foreach (var seed in _seeds)
        {
            var count = seed.GetProjectParameter(FamilySeed.Type1, CountDuplicate);
            await Assert.That(count).IsNotNull();

            // The authored formula must compute: Длина_Дубль = Ширина * 2.
            var length = seed.GetProjectParameter(FamilySeed.Type1, LengthDuplicate);
            await Assert.That(length).IsNotNull();
            await Assert.That(
                Math.Abs(length!.AsDouble() - FamilySeed.Type1WidthMm / 304.8 * 2) < 1e-6).IsTrue();
        }

        // The second preview over the same families is an honest all-skip: the
        // migration is idempotent, existing names are not errors.
        var secondPreview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "add_family_parameter",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options()
            },
            Doc);
        await Assert.That(secondPreview.Success).IsTrue();
        var secondJobId = secondPreview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));
        var secondRecord = await Store.GetAsync(secondJobId, CancellationToken.None);
        var secondData = JobJson.Deserialize<JobPreview>(secondRecord!.PreviewJson)!;
        await Assert.That(secondData.WillChangeCount).IsEqualTo(0);
        await Assert.That(secondData.SkipCount).IsEqualTo(FamilyCount);

        var rollback = Runner.Rollback(Doc, jobId, dryRun: false);
        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);
        await Assert.That(rollback.Restored).IsEqualTo(FamilyCount);
        await Assert.That(rollback.RestoreFailed).IsEqualTo(0);

        foreach (var seed in _seeds)
        {
            await Assert.That(seed.GetProjectParameter(FamilySeed.Type1, CountDuplicate)).IsNull();
            await Assert.That(seed.GetProjectParameter(FamilySeed.Type1, LengthDuplicate)).IsNull();
        }

        await Assert.That(Doc.Application.Documents.Cast<Document>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task ScopeJob_SwitchesInstanceToType_KeepsValuesAndRollsBack()
    {
        if (!SeedFamilies())
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        var spec = ScopeSpec(parameter: FamilySeed.InstanceParam, isInstance: false);
        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_family_parameter_scope",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options()
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.WillChangeCount).IsEqualTo(FamilyCount);

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_family_parameter_scope",
                Mode = JobMode.Apply,
                SpecJson = spec,
                PreviewDigest = previewData.Digest,
                Options = Options()
            },
            Doc);

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));
        await Assert.That(result.State).IsEqualTo(JobState.Completed);

        // After MakeType the parameter is visible on the family SYMBOL
        // (project-side proof of the scope switch).
        foreach (var seed in _seeds)
        {
            var parameter = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.InstanceParam);
            await Assert.That(parameter).IsNotNull();
        }

        var rollback = Runner.Rollback(Doc, jobId, dryRun: false);
        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);
        await Assert.That(rollback.Restored).IsEqualTo(FamilyCount);

        // Back to instance scope: the symbol no longer carries it.
        foreach (var seed in _seeds)
        {
            var parameter = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.InstanceParam);
            await Assert.That(parameter).IsNull();
        }

        await Assert.That(Doc.Application.Documents.Cast<Document>().Count()).IsEqualTo(1);
    }

    private bool SeedFamilies()
    {
        for (var index = 1; index <= FamilyCount; index++)
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

    private static JobOptions Options() => new()
    {
        SavePolicy = JobSavePolicy.Never,
        BatchSize = 2,
        SliceBudgetMs = 1500,
        MaxItems = 500
    };

    private static string AddSpec() =>
        "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"parameters\":[" +
        "{\"name\":\"" + CountDuplicate + "\",\"storageType\":\"number\"}," +
        "{\"name\":\"" + LengthDuplicate + "\",\"storageType\":\"length\"," +
        "\"formula\":\"" + FamilySeed.WidthParam + " * 2\"}]}";

    private static string ScopeSpec(string parameter, bool isInstance) =>
        "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"}," +
        "\"parameterRef\":{\"name\":\"" + parameter + "\"},\"isInstance\":" +
        (isInstance ? "true" : "false") + "}";
}
