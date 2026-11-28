using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;
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
            new IJobAction[] { new MapFamilyParametersJobAction(), new AddFamilyParametersJobAction() },
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
    public async Task FamilyJob_CreateTargetWithFormula_CreatesAndCopiesFormula()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFile();
        const string targetName = "Площадь_новая";
        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"mappings\":[" +
            "{\"source\":{\"name\":\"" + FamilySeed.AreaParam + "\"},\"target\":{\"name\":\"" +
            targetName + "\"},\"mode\":\"formula\",\"createTarget\":true}]}";

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
        await Assert.That(previewData.FailedCount).IsEqualTo(0);
        // One family unit carries the creation and the formula together.
        await Assert.That(previewData.WillChangeCount).IsEqualTo(1);

        var previewItems = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        using var expected = System.Text.Json.JsonDocument.Parse(previewItems[0].ExpectedJson!);
        var creates = expected.RootElement.GetProperty("wouldCreateTargets");
        await Assert.That(creates.GetArrayLength()).IsEqualTo(1);
        await Assert.That(creates[0].GetProperty("name").GetString()).IsEqualTo(targetName);

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
        var area = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.AreaParam);
        await Assert.That(created).IsNotNull();
        await Assert.That(area).IsNotNull();
        await Assert.That(System.Math.Abs(created!.AsDouble() - area!.AsDouble()) < 1e-9).IsTrue();
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

    [Test]
    public async Task FamilyJob_SkipIfSourceMissing_SkipsMappingInsteadOfFailingUnit()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFile();
        // Live feedback v17: one universal mapping list over families with
        // different signatures — the family has Ширина but no НетТакогоПараметра,
        // and that second mapping must become a reported skip, not a unit failure.
        const string targetName = "ЦельНовая";
        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"mappings\":[" +
            "{\"source\":{\"name\":\"Ширина\"},\"target\":{\"name\":\"" + targetName +
            "\"},\"createTarget\":true}," +
            "{\"source\":{\"name\":\"НетТакогоПараметра\"},\"target\":{\"name\":\"ЦельПропущена\"}," +
            "\"createTarget\":true,\"skipIfSourceMissing\":true}]}";

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
        await Assert.That(previewData.FailedCount).IsEqualTo(0);
        await Assert.That(previewData.WillChangeCount).IsEqualTo(1);

        var previewItems = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        using var expected = System.Text.Json.JsonDocument.Parse(previewItems[0].ExpectedJson!);
        // Only the first mapping materialized: the skipped one created no target.
        var creates = expected.RootElement.GetProperty("wouldCreateTargets");
        await Assert.That(creates.GetArrayLength()).IsEqualTo(1);
        await Assert.That(creates[0].GetProperty("name").GetString()).IsEqualTo(targetName);
        await Assert.That(previewItems[0].PreviewJson!.Contains("НетТакогоПараметра")).IsTrue();
        await Assert.That(previewItems[0].PreviewJson!.Contains("skipped")).IsTrue();

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
        await Assert.That(seed.GetProjectParameter(FamilySeed.Type1, targetName)).IsNotNull();
        await Assert.That(seed.GetProjectParameter(FamilySeed.Type1, "ЦельПропущена")).IsNull();
    }

    [Test]
    public async Task FamilyJob_FormulaRepointed_ReauthorsFormulaOverTargetNames()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFile();

        // The source formula references ANOTHER mapped parameter: after
        // repointing it must reference the TARGET name (KRTRS-style chain,
        // not the source chain). Cross-mapping references are why the apply
        // creates all targets in a first pass.
        var seed = _seeds[0];
        var familyElement = Doc.GetElement(ElementIdCompat.Create(seed.FamilyId)) as Autodesk.Revit.DB.Family;
        await Assert.That(familyElement).IsNotNull();
        var setupOutcome = new WriteOutcome();
        using (var setup = AgentFamilySession.Open(Doc, familyElement!, setupOutcome))
        {
            await Assert.That(setup).IsNotNull();
            var manager = setup!.FamilyDoc.FamilyManager;
            using (var seedFormula = new Transaction(setup.FamilyDoc, "Seed source formula"))
            {
                seedFormula.Start();
                manager.SetFormula(
                    AgentFamilyReader.FindParameter(manager, FamilySeed.WidthParam, null, null, out _, out _)!,
                    FamilySeed.HeightParam + " * 2");
                seedFormula.Commit();
            }

            // The session closes the family document without saving on Dispose;
            // load the formula back into the project or the job will not see it.
            await Assert.That(setup.TryLoadBack(setupOutcome)).IsTrue();
        }

        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"mappings\":[" +
            "{\"source\":{\"name\":\"" + FamilySeed.HeightParam + "\"},\"target\":{\"name\":\"ЦельВысота\"},\"createTarget\":true}," +
            "{\"source\":{\"name\":\"" + FamilySeed.WidthParam + "\"},\"target\":{\"name\":\"ЦельШирина\"},\"createTarget\":true,\"mode\":\"formula_repointed\"}]}";

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

        if (!preview.Success)
        {
            throw new InvalidOperationException(
                "PREVIEW FAILED: " + preview.Error?.Code + ": " + preview.Error?.Message);
        }

        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        if (previewData.FailedCount != 0)
        {
            throw new InvalidOperationException(
                "PREVIEW UNIT FAILED: " + string.Join(" | ", previewData.Sample.Select(s =>
                    s.ErrorCode + ": " + (s.ErrorDetail ?? string.Empty))));
        }

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

        // The repointed formula lives on the target and follows the TARGET of
        // the other mapping: ЦельШирина = ЦельВысота * 2 (equal per type).
        var widthParam = seed.GetProjectParameter(FamilySeed.Type1, "ЦельШирина");
        await Assert.That(widthParam).IsNotNull();
        var heightTarget = seed.GetProjectParameter(FamilySeed.Type1, "ЦельВысота");
        await Assert.That(heightTarget).IsNotNull();
        await Assert.That(Math.Abs(widthParam!.AsDouble() - heightTarget!.AsDouble() * 2) < 1e-6).IsTrue();
    }

    [Test]
    public async Task FamilyJob_NbspFamilyName_ResolvesThroughSelector()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFile();

        // Live v18: a family whose stored name carries a NON-BREAKING space
        // resolved intermittently (the model sends a plain space, Revit
        // stores NBSP). Rename the seeded family to an NBSP name, then
        // address it by the plain-space name — the selector must match.
        var seed = _seeds[0];
        using (var renameTx = new Transaction(Doc, "Rename family with NBSP"))
        {
            renameTx.Start();
            var familyElement = Doc.GetElement(ElementIdCompat.Create(seed.FamilyId)) as Autodesk.Revit.DB.Family;
            await Assert.That(familyElement).IsNotNull();
            familyElement!.Name = FamilyPrefix + "1" + " " + "Premim WW";
            renameTx.Commit();
        }

        const string targetName = "ЦельНБСП";
        var spec =
            "{\"select\":{\"familyNames\":[\"" + FamilyPrefix + "1 Premim WW\"]},\"mappings\":[" +
            "{\"source\":{\"name\":\"" + FamilySeed.WidthParam + "\"},\"target\":{\"name\":\"" + targetName +
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

        if (!preview.Success)
        {
            throw new InvalidOperationException(
                "NBSP PREVIEW FAILED: " + preview.Error?.Code + ": " + preview.Error?.Message);
        }

        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.Count).IsEqualTo(1);
        await Assert.That(previewData.FailedCount).IsEqualTo(0);

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

        // seed.GetProjectParameter/FamilyId look the family up by the ORIGINAL
        // name/id, both stale after the rename and the reload — find the
        // reloaded family by its NBSP name fragment.
        Autodesk.Revit.DB.Family? reloaded = null;
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(Autodesk.Revit.DB.Family)))
        {
            if (element is Autodesk.Revit.DB.Family candidate
                && candidate.Name.Contains("Premim WW", StringComparison.Ordinal))
            {
                reloaded = candidate;
            }
        }

        await Assert.That(reloaded).IsNotNull();
        Parameter? created = null;
        Parameter? widthOnType = null;
        foreach (var typeId in reloaded!.GetFamilySymbolIds())
        {
            if (Doc.GetElement(typeId) is FamilySymbol symbol
                && symbol.Name == FamilySeed.Type1)
            {
                created = symbol.LookupParameter(targetName);
                widthOnType = symbol.LookupParameter(FamilySeed.WidthParam);
            }
        }

        await Assert.That(created).IsNotNull();
        // Units-independent: the copied value must match the source on the
        // same type (as in FamilyJob_CreateTarget_CreatesAndMaps).
        await Assert.That(Math.Abs(created!.AsDouble() - widthOnType!.AsDouble()) < 1e-9).IsTrue();
    }

    [Test]
    public async Task FamilyJob_PlacedOnly_RefusesUnplacedSelection()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        // Live v18: seeded families have no placed instances, so the placed
        // selector must refuse the expansion instead of silently sizing the
        // job to the whole (unplaced) library.
        UseSharedParameterFile();
        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\",\"placedOnly\":true},\"mappings\":[" +
            "{\"source\":{\"name\":\"" + FamilySeed.WidthParam + "\"},\"target\":{\"name\":\"ЦельШирина\"},\"createTarget\":true}]}";

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

        // The expansion refuses at Start: nothing is placed in this test
        // document, so placedOnly must not silently size a job to zero units
        // either — it is an honest selector_empty with the next action.
        if (!preview.Success)
        {
            await Assert.That(preview.Error?.Code).IsEqualTo(JobErrorCodes.SelectorEmpty);
            await Assert.That(preview.Error!.Message.Contains("placedOnly")).IsTrue();
            return;
        }

        throw new InvalidOperationException(
            "PLACEDONLY DID NOT REFUSE: the job started with state " + preview.Job!.State);
    }

    [Test]
    public async Task ProjectBinding_SameGuid_MergesWithFamilyValue()
    {
        // Owner's challenge (live v18): does a project binding of the SAME
        // shared parameter shadow the family value on a project-side
        // read-back? Autodesk (support article "Behavior and risks when the
        // same shared parameter exists in both family and a project"): the
        // family parameter and the binding MERGE into one parameter when the
        // family loads. This test pins it on our stack: after binding the
        // definition to the family category, symbol.LookupParameter must
        // still return the FAMILY value — the migration verify path relies
        // on exactly that.
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFile();
        const string mergeParamName = "ПараметрСлив";
        var seed = _seeds[0];
        var familyElement = Doc.GetElement(ElementIdCompat.Create(seed.FamilyId)) as Autodesk.Revit.DB.Family;
        await Assert.That(familyElement).IsNotNull();

        // 1) Add the shared parameter to the family and set a per-type value.
        var setupOutcome = new WriteOutcome();
        using (var setup = AgentFamilySession.Open(Doc, familyElement!, setupOutcome))
        {
            await Assert.That(setup).IsNotNull();
            var file = Doc.Application.OpenSharedParameterFile();
            await Assert.That(file).IsNotNull();
            var group = file!.Groups.Create("SC_Merge");
            var creationOptions = new ExternalDefinitionCreationOptions(mergeParamName, SpecTypeId.String.Text);
            var externalDefinition = group.Definitions.Create(creationOptions) as ExternalDefinition;
            await Assert.That(externalDefinition).IsNotNull();

            var manager = setup!.FamilyDoc.FamilyManager;
            using (var tx = new Transaction(setup.FamilyDoc, "Add merge parameter"))
            {
                tx.Start();
                var created = manager.AddParameter(externalDefinition!, GroupTypeId.General, false);
                foreach (var type in AgentFamilyReader.GetTypes(manager))
                {
                    if (type.Name != FamilySeed.Type1)
                    {
                        continue;
                    }

                    manager.CurrentType = type;
                    manager.Set(created, "ABC");
                }

                tx.Commit();
            }

            await Assert.That(setup.TryLoadBack(setupOutcome)).IsTrue();
        }

        // 2) Bind the same definition to the family category in the project.
        var familyAfter = Doc.GetElement(ElementIdCompat.Create(seed.FamilyId)) as Autodesk.Revit.DB.Family;
        await Assert.That(familyAfter).IsNotNull();
        var categorySet = Doc.Application.Create.NewCategorySet();
        categorySet.Insert(familyAfter!.FamilyCategory);
        var bindingFile = Doc.Application.OpenSharedParameterFile();
        await Assert.That(bindingFile).IsNotNull();
        Autodesk.Revit.DB.Definition? bindingDefinition = null;
        foreach (var groupCandidate in bindingFile!.Groups)
        {
            foreach (var definitionCandidate in groupCandidate.Definitions)
            {
                if (definitionCandidate.Name == mergeParamName
                    && definitionCandidate is ExternalDefinition asExternal)
                {
                    bindingDefinition = asExternal;
                }
            }
        }

        await Assert.That(bindingDefinition).IsNotNull();
        using (var tx = new Transaction(Doc, "Bind merge parameter"))
        {
            tx.Start();
            var instanceBinding = Doc.Application.Create.NewInstanceBinding(categorySet);
            var inserted = Doc.ParameterBindings.Insert(
                bindingDefinition!, instanceBinding, GroupTypeId.General);
            await Assert.That(inserted).IsTrue();
            tx.Commit();
        }

        // 3) The project-side read-back (the verify path): family value wins.
        var value = seed.GetProjectParameter(FamilySeed.Type1, mergeParamName);
        await Assert.That(value).IsNotNull();
        await Assert.That(value!.AsString()).IsEqualTo("ABC");
    }

    [Test]
    public async Task FamilyFailuresSuppressor_ResolvesWhitelistedPromptAndReports()
    {
        // Live v18: «Семейство больше не соответствует свойствам системы…» —
        // the prompt the owner keeps confirming manually. What is under test
        // here is the suppression path itself: the FailuresProcessing event
        // fires and the whitelist matches the posted failure. An empty
        // document gives the failure no resolutions (no family, no system),
        // so the actual default-button press only happens on a live model;
        // WhitelistMatches is the honest vacuum-proof signal.
        using var suppressor = AgentFamilyFailuresSuppressor.Begin(Doc);
        using var transaction = new Transaction(Doc, "SmartCon test: failures suppressor");
        transaction.Start();
        Doc.PostFailure(new FailureMessage(
            BuiltInFailures.FamilyFailures.FamilyDoesntMatchSystemPropertiesDisconnected));
        _ = transaction.Commit();

        await Assert.That(suppressor.WhitelistMatches).IsGreaterThan(0);
    }

    [Test]
    public async Task FamilyJob_NoOpUnit_IsSkippedNotMismatch()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFile();

        // Live v18 final migration run: a family with no source parameters
        // skipped every mapping (skipIfSourceMissing) — nothing was written.
        // The false-done guard (FinalizeJob re-verifies Done AND Skipped units)
        // turned the empty expected state into a false job_mismatch: the job
        // ended CompletedWithFailures and retry_failed_items could never pick
        // the unit. An honest no-op must stay Skipped and the job Completed.
        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"mappings\":[" +
            "{\"source\":{\"name\":\"НетТакогоПараметра\"},\"target\":{\"name\":\"ЦельNoOp\"}," +
            "\"createTarget\":true,\"skipIfSourceMissing\":true}]}";

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
        await Assert.That(previewData.WillChangeCount).IsEqualTo(0);

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
        await Assert.That(applyItems.Count(item => item.State == JobItemState.Skipped)).IsEqualTo(1);
        await Assert.That(applyItems.Count(item => item.State == JobItemState.Mismatch)).IsEqualTo(0);

        var seed = _seeds[0];
        await Assert.That(seed.GetProjectParameter(FamilySeed.Type1, "ЦельNoOp")).IsNull();
    }

    [Test]
    public async Task FamilyJob_ValuesOnFormulaSource_AutoEscalatesAndCarriesFormula()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFile();

        // Live migration run: the model planned a values snapshot for every
        // computed field. The job no longer depends on the model choosing the
        // mode: mode=values on a formula-driven source AUTO-ESCALATES to
        // formula_repointed, so the duplicate recalculates; the report also
        // carries FormulaTransferred (the proof the formula lives).
        var seed = _seeds[0];
        var familyElement = Doc.GetElement(ElementIdCompat.Create(seed.FamilyId)) as Autodesk.Revit.DB.Family;
        await Assert.That(familyElement).IsNotNull();
        var setupOutcome = new WriteOutcome();
        using (var setup = AgentFamilySession.Open(Doc, familyElement!, setupOutcome))
        {
            await Assert.That(setup).IsNotNull();
            var manager = setup!.FamilyDoc.FamilyManager;
            using (var seedFormula = new Transaction(setup.FamilyDoc, "Seed source formula"))
            {
                seedFormula.Start();
                manager.SetFormula(
                    AgentFamilyReader.FindParameter(manager, FamilySeed.WidthParam, null, null, out _, out _)!,
                    FamilySeed.HeightParam + " * 2");
                seedFormula.Commit();
            }

            // The session closes the family document without saving on Dispose;
            // load the formula back into the project or the job will not see it.
            await Assert.That(setup.TryLoadBack(setupOutcome)).IsTrue();
        }

        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"mappings\":[" +
            "{\"source\":{\"name\":\"" + FamilySeed.WidthParam + "\"},\"target\":{\"name\":\"ЦельСрез\"}," +
            "\"createTarget\":true,\"mode\":\"values\"}]}";

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

        var previewItems = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(previewItems[0].PreviewJson!.Contains("FORMULA-DRIVEN")).IsTrue();
        await Assert.That(previewItems[0].PreviewJson!.Contains("auto-escalated")).IsTrue();
        // The structural aggregate lives on the preview ENVELOPE (jobs.preview_json),
        // built across EVERY unit — the formula pass is built from it.
        var previewData = JobJson.Deserialize<JobPreview>(
            (await Store.GetAsync(jobId, CancellationToken.None))!.PreviewJson)!;
        if (previewData.FormulaDrivenSources is null
            || !previewData.FormulaDrivenSources.ContainsKey(FamilySeed.WidthParam))
        {
            throw new InvalidOperationException(
                "formulaDrivenSources missing in the preview envelope: " +
                (previewData.FormulaDrivenSources is null
                    ? "<null>"
                    : string.Join(", ", previewData.FormulaDrivenSources.Select(
                        pair => pair.Key + "=" + pair.Value))) +
                "; unit json: " + previewItems[0].PreviewJson);
        }

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
        var applyItems = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        if (result.State != JobState.Completed)
        {
            throw new InvalidOperationException(
                "JOB ENDED " + result.State + ": " +
                string.Join(" | ", applyItems.Select(item =>
                    item.State + " " + item.ErrorCode + " " + item.ErrorDetail)) +
                "; actual: " + (applyItems[0].ActualJson ?? "<null>"));
        }

        await Assert.That(applyItems[0].ActualJson!.Contains("formulaTransferred")).IsTrue();

        // Read-back: the created target IS formula-driven with the source
        // formula (the references stayed, this mapping had no other pairs).
        familyElement = Doc.GetElement(ElementIdCompat.Create(seed.FamilyId)) as Autodesk.Revit.DB.Family;
        await Assert.That(familyElement).IsNotNull();
        using (var readBack = AgentFamilySession.Open(Doc, familyElement!, setupOutcome))
        {
            await Assert.That(readBack).IsNotNull();
            var target = AgentFamilyReader.FindParameter(
                readBack!.FamilyDoc.FamilyManager, "ЦельСрез", null, null, out _, out _);
            await Assert.That(target).IsNotNull();
            if (!AgentFamilyReader.IsFormulaDriven(target!)
                || target.Formula != FamilySeed.HeightParam + " * 2")
            {
                var sourceParameter = AgentFamilyReader.FindParameter(
                    readBack.FamilyDoc.FamilyManager, FamilySeed.WidthParam, null, null, out _, out _);
                throw new InvalidOperationException(
                    "READ-BACK target '" + target.Definition.Name + "': IsDeterminedByFormula=" +
                    target.IsDeterminedByFormula + ", formula='" + target.Formula +
                    "', sourceFormula='" + sourceParameter?.Formula + "'");
            }
        }
    }

    [Test]
    public async Task FamilyJob_AddMatchInstanceSource_OneJobAcrossScopes()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFile();

        // Live v18 wave 7: the vendor library mixes type/instance scope for the
        // same parameter names, and a fixed isInstance fragmented the migration
        // into 17 signature groups. matchInstanceSource inherits the scope from
        // THIS family's source parameter — ONE job covers everything.
        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"parameters\":[" +
            "{\"name\":\"ЦельТипСкоуп\",\"storageType\":\"text\",\"matchInstanceSource\":\"" + FamilySeed.WidthParam + "\"}," +
            "{\"name\":\"ЦельЭкземпляр\",\"storageType\":\"text\",\"matchInstanceSource\":\"" + FamilySeed.InstanceParam + "\"}]}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "add_family_parameter",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options(batchSize: 1)
            },
            Doc);

        if (!preview.Success)
        {
            throw new InvalidOperationException(
                "PREVIEW FAILED: " + preview.Error?.Code + ": " + preview.Error?.Message);
        }
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewData = JobJson.Deserialize<JobPreview>((await Store.GetAsync(jobId, CancellationToken.None))!.PreviewJson)!;
        await Assert.That(previewData.FailedCount).IsEqualTo(0);

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "add_family_parameter",
                Mode = JobMode.Apply,
                SpecJson = spec,
                PreviewDigest = previewData.Digest,
                Options = Options(batchSize: 1)
            },
            Doc);

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));
        var applyItems = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        if (result.State is not (JobState.Completed or JobState.CompletedUnverified))
        {
            throw new InvalidOperationException(
                "JOB ENDED " + result.State + ": " +
                string.Join(" | ", applyItems.Select(item =>
                    item.State + " " + item.ErrorCode + " " + item.ErrorDetail)));
        }

        // The type-scope duplicate sits on the project symbol; the instance one
        // does not (project symbols carry type parameters only).
        var seed = _seeds[0];
        await Assert.That(seed.GetProjectParameter(FamilySeed.Type1, "ЦельТипСкоуп")).IsNotNull();
        await Assert.That(seed.GetProjectParameter(FamilySeed.Type1, "ЦельЭкземпляр")).IsNull();

        // And the instance-scope duplicate really exists in the family doc.
        var familyElement = Doc.GetElement(ElementIdCompat.Create(seed.FamilyId)) as Autodesk.Revit.DB.Family;
        await Assert.That(familyElement).IsNotNull();
        var outcome = new WriteOutcome();
        using (var session = AgentFamilySession.Open(Doc, familyElement!, outcome))
        {
            await Assert.That(session).IsNotNull();
            var instanceTarget = AgentFamilyReader.FindParameter(
                session!.FamilyDoc.FamilyManager, "ЦельЭкземпляр", null, null, out _, out _);
            await Assert.That(instanceTarget).IsNotNull();
            await Assert.That(instanceTarget!.IsInstance).IsTrue();
        }
    }

    [Test]
    public async Task FamilyJob_NestedSharedFamily_MigratesAsUnit()
    {
        // Live migration feedback: shared nested families (flanges, bolts, nuts)
        // schedule as separate rows but stayed out of the migration plan. A
        // shared nested family IS an ordinary project family: it resolves
        // through the placedOnly selector (its instances live inside hosts) and
        // migrates like any unit — one reload updates every host (Autodesk
        // forum + help; pinned here end to end).
        const string nestedName = "SC_NestedShared";
        const string hostName = "SC_NestedHost";
        const string nestedWidth = "Влож_Ширина";
        const string nestedHeight = "Влож_Высота";
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
        {
            Skip.Test("В установленном Revit нет подходящего .rft шаблона семейства");
        }

        var directory = Path.Combine(Path.GetTempPath(), "SmartConFamilyTests");
        Directory.CreateDirectory(directory);
        // A Guid suffix keeps parallel hosts (R25 + net48) and a locked file of
        // a crashed previous run from hard-failing the seed.
        var runSuffix = Guid.NewGuid().ToString("N")[..8];
        var nestedPath = Path.Combine(directory, nestedName + "_" + runSuffix + ".rfa");
        var hostPath = Path.Combine(directory, hostName + "_" + runSuffix + ".rfa");
        foreach (var path in new[] { nestedPath, hostPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        var nestedFamilyName = nestedName;
        Autodesk.Revit.DB.Family? loadedNestedOut = null;
        Document? familyDoc = null;
        try
        {
            // 1. The nested family B (no shared flag yet: the flag flips on the
            //    nested copy inside the host, see the transaction below).
            familyDoc = Application.NewFamilyDocument(template);
            using (var nestedTransaction = new Transaction(familyDoc, "Seed nested family"))
            {
                nestedTransaction.Start();
                var nestedManager = familyDoc.FamilyManager;
                var nestedWidthParameter = nestedManager.AddParameter(
                    nestedWidth, GroupTypeId.General, SpecTypeId.Length, false);
                var nestedHeightParameter = nestedManager.AddParameter(
                    nestedHeight, GroupTypeId.General, SpecTypeId.Length, false);
                // A fresh template document has NO current type (FamilySeed does
                // the same dance): NewType creates one and makes it current.
                if (nestedManager.CurrentType is null)
                {
                    nestedManager.NewType("ВложТип");
                }

                nestedManager.Set(nestedWidthParameter, 500.0 / 304.8);
                nestedManager.Set(nestedHeightParameter, 300.0 / 304.8);
                nestedTransaction.Commit();
            }

            familyDoc.SaveAs(nestedPath, new SaveAsOptions { OverwriteExistingFile = true });
            familyDoc.Close(false);
            familyDoc = null;

            // 2. The host family document with B nested inside. The Tammik
            //    nested-family recipe and the Autodesk nested-generation thread
            //    load the .rfa by path with LoadFamily(filename, out family)
            //    into a receiver opened FROM FILE: a NewFamilyDocument receiver
            //    refuses the load (measured), the Document-overload nests
            //    EMPTY into a family document (measured), and
            //    LoadFamily(String) alone returns false there (measured).
            var hostSeedDoc = Application.NewFamilyDocument(template);
            hostSeedDoc.SaveAs(hostPath, new SaveAsOptions { OverwriteExistingFile = true });
            hostSeedDoc.Close(false);
            familyDoc = Application.OpenDocumentFile(hostPath);
            var nestedLoaded = false;
            using (var loadNestedTransaction = new Transaction(familyDoc, "Load nested family"))
            {
                loadNestedTransaction.Start();
                nestedLoaded = familyDoc.LoadFamily(nestedPath, out var loadedNested);
                loadNestedTransaction.Commit();
                loadedNestedOut = loadedNested;
            }

            await Assert.That(nestedLoaded).IsTrue();
            await Assert.That(loadedNestedOut).IsNotNull();

            Autodesk.Revit.DB.FamilySymbol? nestedSymbol = null;
            foreach (var element in new FilteredElementCollector(familyDoc)
                         .OfClass(typeof(Autodesk.Revit.DB.FamilySymbol)))
            {
                if (element is Autodesk.Revit.DB.FamilySymbol candidate
                    && candidate.LookupParameter(nestedWidth) is not null)
                {
                    nestedSymbol = candidate;
                    break;
                }
            }

            await Assert.That(nestedSymbol).IsNotNull();
            nestedFamilyName = nestedSymbol!.Family.Name;

            using (var nestTransaction = new Transaction(familyDoc, "Nest shared family"))
            {
                nestTransaction.Start();
                // No Family.IsShared in the API: the built-in parameter is the
                // only way to mark the family Shared (forum-confirmed). The
                // flag flips on the nested copy inside the host document.
                nestedSymbol.Family
                    .get_Parameter(BuiltInParameter.FAMILY_SHARED)!
                    .Set(1);
                nestedSymbol.Activate();
                familyDoc.Regenerate();
                familyDoc.FamilyCreate.NewFamilyInstance(
                    new XYZ(0, 0, 0), nestedSymbol,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                nestTransaction.Commit();
            }

            familyDoc.SaveAs(hostPath, new SaveAsOptions { OverwriteExistingFile = true });
            familyDoc.Close(false);
            familyDoc = null;
        }
        finally
        {
            if (familyDoc is not null && familyDoc.IsValidObject)
            {
                AgentFamilyDocCloser.Close(familyDoc);
            }
        }

        // 3b. Load the host into the project: the SHARED nested family comes
        //     along as an ordinary project family.
        var hostLoaded = false;
        Autodesk.Revit.DB.Family? loadedHostFamily = null;
        using (var loadTransaction = new Transaction(Doc, "Load nested host"))
        {
            loadTransaction.Start();
            hostLoaded = Doc.LoadFamily(hostPath, out loadedHostFamily);
            loadTransaction.Commit();
        }

        await Assert.That(hostLoaded).IsTrue();
        await Assert.That(loadedHostFamily).IsNotNull();

        // 4. Place a host instance: the nested shared family's instances live
        //    inside host instances — that is the whole point of the test. The
        //    loaded family reference itself addresses the host (the file name
        //    carries a run suffix, so a name lookup would need normalization).
        Autodesk.Revit.DB.FamilySymbol? hostSymbol = null;
        foreach (var symbolId in loadedHostFamily!.GetFamilySymbolIds())
        {
            hostSymbol = Doc.GetElement(symbolId) as Autodesk.Revit.DB.FamilySymbol;
            break;
        }

        await Assert.That(hostSymbol).IsNotNull();
        using (var placeTransaction = new Transaction(Doc, "Place host instance"))
        {
            placeTransaction.Start();
            hostSymbol!.Activate();
            Doc.Regenerate();
            Doc.Create.NewFamilyInstance(
                new XYZ(0, 0, 0), hostSymbol,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            placeTransaction.Commit();
        }

        // 5. The placedOnly selector must resolve the nested shared family —
        //    it has NO direct instances; every instance lives inside hosts.
        var selection = FamilyJobSelectors.Resolve(
            Doc,
            null,
            null,
            new[] { nestedFamilyName },
            null,
            null,
            null,
            null,
            placedOnly: true);
        await Assert.That(selection.Success).IsTrue();
        await Assert.That(selection.Families.Count).IsEqualTo(1);
        await Assert.That(selection.Families[0].Name).IsEqualTo(nestedFamilyName);
        await Assert.That(
            selection.Warnings.Any(warning => warning.Contains("SHARED", StringComparison.Ordinal)))
            .IsTrue();

        // 6. And it migrates as an ordinary unit.
        var spec =
            "{\"select\":{\"familyNames\":[\"" + nestedFamilyName + "\"]},\"mappings\":[" +
            "{\"source\":{\"name\":\"" + nestedWidth + "\"},\"target\":{\"name\":\"" + nestedHeight + "\"}}]}";

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
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord!.PreviewJson)!;
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

        // 7. Read-back through the PROJECT symbol of the nested shared family:
        //    the value followed the target parameter.
        Autodesk.Revit.DB.Family? nestedProjectFamily = null;
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(Autodesk.Revit.DB.Family)))
        {
            if (element is Autodesk.Revit.DB.Family candidate
                && string.Equals(candidate.Name, nestedFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                nestedProjectFamily = candidate;
                break;
            }
        }

        await Assert.That(nestedProjectFamily).IsNotNull();
        var nestedTypeSymbol = Doc.GetElement(
            nestedProjectFamily!.GetFamilySymbolIds().First()) as FamilySymbol;
        await Assert.That(nestedTypeSymbol).IsNotNull();
        var widthValue = nestedTypeSymbol!.LookupParameter(nestedWidth)?.AsDouble();
        var heightValue = nestedTypeSymbol.LookupParameter(nestedHeight)?.AsDouble();
        await Assert.That(widthValue).IsNotNull();
        await Assert.That(heightValue).IsNotNull();
        await Assert.That(Math.Abs(widthValue!.Value - heightValue!.Value) < 1e-9).IsTrue();
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