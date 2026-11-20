using System.Diagnostics;
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
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// End-to-end job engine tests on a real seeded model: 500 views/levels,
/// preview → apply → read-back, cancel, crash resume, report and rollback.
/// </summary>
public sealed partial class AgentProjectJobTests : RevitApiTest
{
    private const int ViewCount = 500;
    private const int LevelCount = 500;
    private const string ViewPrefix = "SC_Job_View_";
    private const string LevelPrefix = "SC_Job_Level_";
    private const string RenameSpec =
        "{\"select\":{\"nameContains\":\"SC_Job_\"},\"rename\":{\"find\":\"SC_Job_View_\",\"replace\":\"SC_Job_Fixed_\"}}";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private AgentDatabase? _database;
    private string? _databasePath;
    private SqliteAgentJobStore? _store;
    private IJobAction[]? _actions;
    private JobRunner? _runner;
    private long _levelId;
    private static readonly System.Collections.Concurrent.ConcurrentBag<string> TempProjectPaths = new();

    private Document Doc => _document!;

    private string DocumentId => AgentDocumentIdentity.GetDocumentId(Doc);

    private SqliteAgentJobStore Store => _store!;

    private JobRunner Runner => _runner!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
        {
            Skip.Test("В шаблоне проекта нет ViewFamilyType для плана этажа");
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "sc-job-" + Guid.NewGuid().ToString("N") + ".db");
        _database = new AgentDatabase(_databasePath);
        _database.Initialize();
        _store = new SqliteAgentJobStore(_database);

        _actions = new IJobAction[]
        {
            new RenameViewsJobAction(),
            new SetParametersJobAction()
        };
        _runner = new JobRunner(_store, _actions, _transactions);

        _transactions.RunInTransaction(Doc, "Seed job views", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0);
            _levelId = level.Id.GetValue();
            for (var index = 1; index <= ViewCount; index++)
            {
                ModelSeed.CreateViewPlanChecked(doc, level.Id, ViewPrefix + index);
            }
        });
    }

    [After(Test)]
    public void Cleanup()
    {
        Doc.Close(false);

        if (_databasePath is not null && File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
                // The store closes every connection; a locked file is not fatal for the test.
            }
        }

        while (TempProjectPaths.TryTake(out var savedPath))
        {
            if (!File.Exists(savedPath))
            {
                continue;
            }

            try
            {
                File.Delete(savedPath);
            }
            catch (IOException)
            {
                // The document was closed above; a leftover file is harmless.
            }
        }
    }

    [Test]
    public async Task RenameViewsJob_500Views_CompletesAndVerifies()
    {
        var job = PrepareApplyJob(JobSavePolicy.Never, batchSize: 50);

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        await Assert.That(result.State).IsEqualTo(JobState.Completed);
        await Assert.That(result.Error).IsNull();

        var items = await Store.LoadAllItemsAsync(job.JobId, CancellationToken.None);
        await Assert.That(items.Count).IsEqualTo(ViewCount);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(ViewCount);

        var names = CollectViewNames();
        await Assert.That(names.Contains("SC_Job_Fixed_1")).IsTrue();
        await Assert.That(names.Contains("SC_Job_Fixed_" + ViewCount)).IsTrue();
        await Assert.That(names.Count(name => name.StartsWith("SC_Job_Fixed_"))).IsEqualTo(ViewCount);
    }

    [Test]
    public async Task RenameViewsJob_SecondRun_IsNoOp()
    {
        var first = PrepareApplyJob(JobSavePolicy.Never, batchSize: 50);
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        var afterFirst = CollectViewNames();

        var second = PrepareApplyJob(JobSavePolicy.Never, batchSize: 50);
        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        await Assert.That(result.State).IsEqualTo(JobState.Completed);
        await Assert.That(second.JobId).IsNotEqualTo(first.JobId);

        var items = await Store.LoadAllItemsAsync(second.JobId, CancellationToken.None);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(0);
        await Assert.That(items.Count(item => item.State == JobItemState.Skipped)).IsEqualTo(ViewCount);

        var afterSecond = CollectViewNames();
        await Assert.That(afterSecond.SetEquals(afterFirst)).IsTrue();
    }

    [Test]
    public async Task RenameViewsJob_CancelMidApply_StopsAndReportsPartial()
    {
        var cancelAtSeq = 100;
        var cancelRunner = CreateRunner(new CancelAfterAction(
            new RenameViewsJobAction(),
            cancelAtSeq,
            jobId => Runner.Cancel(jobId, DocumentId)));

        var preview = cancelRunner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 20)),
            Doc);
        await Assert.That(preview.Success).IsTrue();
        cancelRunner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var previewRecord = await Store.GetAsync(preview.Job!.JobId, CancellationToken.None);
        var digest = JobJson.Deserialize<JobPreview>(previewRecord!.PreviewJson)!.Digest;

        cancelRunner.Start(
            RenameViewSpec(JobMode.Apply, digest, Options(JobSavePolicy.Never, 20)),
            Doc);
        var result = cancelRunner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));

        await Assert.That(result.State).IsEqualTo(JobState.Cancelled);

        var items = await Store.LoadAllItemsAsync(preview.Job.JobId, CancellationToken.None);
        var done = items.Count(item => item.State == JobItemState.Done);
        var pending = items.Count(item => item.State == JobItemState.Previewed);

        var report = new JobReportBuilder(Store, _actions!)
            .Build(Doc, preview.Job.JobId, 0, 10, readBack: false, out _);
        await Assert.That(report!.Cancelled).IsTrue();
        await Assert.That(report.InterruptedUnits).IsGreaterThan(0);

        await Assert.That(done).IsGreaterThan(0);
        await Assert.That(done).IsLessThan(ViewCount);
        await Assert.That(pending).IsGreaterThan(0);
    }

    [Test]
    public async Task SetParametersJob_500Walls_SetsComments()
    {
        var wallIds = new List<long>(LevelCount);
        var seeded = true;
        _transactions!.RunInTransaction(Doc, "Seed job walls", doc =>
        {
            for (var index = 1; index <= LevelCount; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, ElementIdCompat.Create(_levelId), index * 0.5);
                if (wall is null)
                {
                    seeded = false;
                    break;
                }

                wallIds.Add(wall.Id.GetValue());
            }
        });

        if (!seeded || wallIds.Count == 0)
        {
            Skip.Test("The project template has no wall type to seed walls");
        }

        var specJson =
            "{\"select\":{\"elementIds\":[" + string.Join(",", wallIds) +
            "]},\"builtInParameter\":\"ALL_MODEL_INSTANCE_COMMENTS\",\"value\":\"checked\",\"scope\":\"instance\"}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Preview,
                SpecJson = specJson,
                Options = Options(JobSavePolicy.Never, 100)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var previewData = JobJson.Deserialize<JobPreview>(preview.Job!.PreviewJson)!;
        if (previewData.WillChangeCount != LevelCount)
        {
            var firstFailure = (await Store.LoadAllItemsAsync(
                    preview.Job.JobId, CancellationToken.None))
                .FirstOrDefault(item => item.State == JobItemState.Failed);
            throw new InvalidOperationException(
                "DIAGNOSTIC willChange=" + previewData.WillChangeCount +
                ", failed=" + previewData.FailedCount +
                ", skipped=" + previewData.SkipCount +
                ", first=" + (firstFailure?.ErrorCode ?? "<none>") + ": " +
                (firstFailure?.ErrorDetail ?? "<none>") +
                ", warnings=" + string.Join(" | ", previewData.Warnings));
        }

        await Assert.That(previewData.WillChangeCount).IsEqualTo(LevelCount);

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Apply,
                SpecJson = specJson,
                PreviewDigest = previewData.Digest,
                Options = Options(JobSavePolicy.Never, 100)
            },
            Doc);

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        await Assert.That(result.State).IsEqualTo(JobState.Completed);

        var written = 0;
        foreach (var wallId in wallIds)
        {
            var parameter = Doc.GetElement(ElementIdCompat.Create(wallId))
                ?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (parameter?.AsString() == "checked")
            {
                written++;
            }
        }

        await Assert.That(written).IsEqualTo(LevelCount);
    }

    [Test]
    public async Task Job_KillSimulated_ResumesWithoutDuplicates()
    {
        var slowRunner = CreateRunner(new SlowAction(new RenameViewsJobAction(), 1));

        var preview = slowRunner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 50)),
            Doc);
        await Assert.That(preview.Success).IsTrue();
        var digest = JobJson.Deserialize<JobPreview>(preview.Job!.PreviewJson)!.Digest;

        slowRunner.Start(
            RenameViewSpec(JobMode.Apply, digest, Options(JobSavePolicy.Never, 50)),
            Doc);

        // Two slices only: the job is still running when the "process dies".
        slowRunner.RunSlice(Doc, TimeSpan.FromMilliseconds(200));
        var beforeCrash = await Store.LoadAllItemsAsync(preview.Job.JobId, CancellationToken.None);
        await Assert.That(beforeCrash.Count(item => item.State == JobItemState.Previewed)).IsGreaterThan(0);

        var restarted = CreateRunner(new RenameViewsJobAction());
        var interrupted = restarted.Reconcile(Doc);

        await Assert.That(interrupted.Count).IsEqualTo(1);

        var resume = restarted.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "rename_views",
                Mode = JobMode.Resume,
                ResumeJobId = preview.Job.JobId,
                SpecJson = RenameSpec,
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);

        await Assert.That(resume.Success).IsTrue();
        var result = restarted.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        await Assert.That(result.State).IsEqualTo(JobState.Completed);

        var items = await Store.LoadAllItemsAsync(preview.Job.JobId, CancellationToken.None);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(ViewCount);

        var names = CollectViewNames();
        await Assert.That(names.Count(name => name.StartsWith("SC_Job_Fixed_"))).IsEqualTo(ViewCount);
    }

    [Test]
    public async Task Job_ReportReadBack_IsTruthful()
    {
        var job = PrepareApplyJob(JobSavePolicy.Never, batchSize: 50);
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        var report = new JobReportBuilder(Store, _actions!)
            .Build(Doc, job.JobId, offset: 0, limit: 50, readBack: true, out _);

        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Truthful).IsTrue();
        await Assert.That(report.MismatchCount).IsEqualTo(0);
        await Assert.That(report.Totals.Done).IsEqualTo(ViewCount);
        await Assert.That(report.Items.Count).IsEqualTo(50);
        await Assert.That(report.HasMore).IsTrue();
        await Assert.That(report.Volatile).IsTrue();
    }

    [Test]
    public async Task Job_Rollback_RestoresOriginalNames()
    {
        var job = PrepareApplyJob(JobSavePolicy.Never, batchSize: 50);
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        var dryRun = Runner.Rollback(Doc, job.JobId, dryRun: true);
        await Assert.That(dryRun.Restored).IsEqualTo(ViewCount);
        await Assert.That(dryRun.RestoreFailed).IsEqualTo(0);

        var rollback = Runner.Rollback(Doc, job.JobId, dryRun: false);

        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);
        await Assert.That(rollback.Restored).IsEqualTo(ViewCount);
        await Assert.That(rollback.RestoreFailed).IsEqualTo(0);

        var names = CollectViewNames();
        await Assert.That(names.Count(name => name.StartsWith(ViewPrefix))).IsEqualTo(ViewCount);
        await Assert.That(names.Count(name => name.StartsWith("SC_Job_Fixed_"))).IsEqualTo(0);
    }

    [Test]
    public async Task Job_NewPreview_SupersedesReadyPreviewWithoutCancel()
    {
        var first = Runner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 50)),
            Doc);
        await Assert.That(first.Success).IsTrue();
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var firstRecord = await Store.GetAsync(first.Job!.JobId, CancellationToken.None);
        await Assert.That(firstRecord!.State).IsEqualTo(JobState.PreviewReady);

        var second = Runner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 50)),
            Doc);
        await Assert.That(second.Success).IsTrue();
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var secondRecord = await Store.GetAsync(second.Job!.JobId, CancellationToken.None);
        await Assert.That(secondRecord!.State).IsEqualTo(JobState.PreviewReady);
        await Assert.That(second.Job.JobId).IsNotEqualTo(first.Job.JobId);

        var old = await Store.GetAsync(first.Job.JobId, CancellationToken.None);
        await Assert.That(old!.State).IsEqualTo(JobState.Cancelled);
        await Assert.That(old.ErrorCode).IsEqualTo(JobErrorCodes.PreviewSuperseded);
    }

    [Test]
    public async Task Job_SecondJobWhileRunning_IsRejected()
    {
        var slowRunner = CreateRunner(new SlowAction(new RenameViewsJobAction(), 1));
        var preview = slowRunner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 50)),
            Doc);
        await Assert.That(preview.Success).IsTrue();
        slowRunner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));

        var previewRecord = await Store.GetAsync(preview.Job!.JobId, CancellationToken.None);
        var digest = JobJson.Deserialize<JobPreview>(previewRecord!.PreviewJson)!.Digest;
        var apply = slowRunner.Start(
            RenameViewSpec(JobMode.Apply, digest, Options(JobSavePolicy.Never, 50)),
            Doc);
        await Assert.That(apply.Success).IsTrue();
        await Assert.That(apply.Job!.State).IsEqualTo(JobState.Running);

        var second = slowRunner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 50)),
            Doc);
        await Assert.That(second.Success).IsFalse();
        await Assert.That(second.Error!.Code).IsEqualTo(JobErrorCodes.JobAlreadyActive);

        slowRunner.Cancel(preview.Job.JobId, DocumentId);
        slowRunner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task Job_UnsavedDocumentWithSavePolicy_FailsClosed()
    {
        var result = Runner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.AfterEachBatch, 50)),
            Doc);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error!.Code).IsEqualTo(JobErrorCodes.DocumentNotSaved);
    }

    [Test]
    public async Task Job_SavePolicy_AfterEachBatch_UpdatesDurableCursor()
    {
        var projectPath = Path.Combine(
            Path.GetTempPath(),
            "sc-job-" + Guid.NewGuid().ToString("N") + ".rvt");
        TempProjectPaths.Add(projectPath);

        try
        {
            Doc.SaveAs(projectPath);
        }
        catch (Exception ex)
        {
            Skip.Test("SaveAs is unavailable in the test host: " + ex.GetType().Name);
        }

        var job = PrepareApplyJob(JobSavePolicy.AfterEachBatch, batchSize: 50);
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        var record = await Store.GetAsync(job.JobId, CancellationToken.None);
        await Assert.That(record).IsNotNull();
        await Assert.That(record!.State).IsEqualTo(JobState.Completed);
        await Assert.That(record.LastSavedCursor).IsEqualTo(ViewCount);
        await Assert.That(record.LastSavedAt).IsNotNull();

        var report = new JobReportBuilder(Store, _actions!)
            .Build(Doc, job.JobId, offset: 0, limit: 10, readBack: true, out _);
        await Assert.That(report!.Saved).IsTrue();
    }

    [Test]
    public async Task Job_SliceLatency_StaysUnderTwoSeconds()
    {
        var job = PrepareApplyJob(JobSavePolicy.Never, batchSize: 50);
        var durations = new List<double>();

        for (var index = 0; index < 60; index++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var slice = Runner.RunSlice(Doc, TimeSpan.FromMilliseconds(200));
            durations.Add(
                (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency);

            if (!slice.HasMoreWork)
            {
                break;
            }
        }

        durations.Sort();
        var p95 = durations[Math.Max(0, (int)Math.Ceiling(durations.Count * 0.95) - 1)];

        var record = await Store.GetAsync(job.JobId, CancellationToken.None);
        await Assert.That(record!.State).IsEqualTo(JobState.Completed);
        await Assert.That(durations.Count).IsGreaterThan(0);
        await Assert.That(p95).IsLessThan(2000.0);
    }

    private JobRecord PrepareApplyJob(JobSavePolicy policy, int batchSize)
    {
        var options = Options(policy, batchSize);

        var preview = Runner.Start(RenameViewSpec(JobMode.Preview, options: options), Doc);
        if (!preview.Success)
        {
            throw new InvalidOperationException("Preview failed: " + preview.Error);
        }

        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        var previewRecord = Store.GetAsync(preview.Job!.JobId, CancellationToken.None)
            .GetAwaiter().GetResult();
        var digest = JobJson.Deserialize<JobPreview>(previewRecord!.PreviewJson)!.Digest;
        var apply = Runner.Start(RenameViewSpec(JobMode.Apply, digest, options), Doc);
        if (!apply.Success)
        {
            throw new InvalidOperationException("Apply failed: " + apply.Error);
        }

        return apply.Job!;
    }

    private JobRunner CreateRunner(IJobAction action) =>
        new(Store, new[] { action }, _transactions!);

    private static JobOptions Options(JobSavePolicy policy, int batchSize) => new()
    {
        SavePolicy = policy,
        BatchSize = batchSize,
        SliceBudgetMs = 200,
        MaxItems = 1000,
        // The seeded 500-unit suites intentionally cross the 200-unit guard.
        AllowLargeJob = true
    };

    private JobSpec RenameViewSpec(JobMode mode, string? digest = null, JobOptions? options = null) =>
        new()
        {
            DocumentId = DocumentId,
            Action = "rename_views",
            Mode = mode,
            SpecJson = RenameSpec,
            PreviewDigest = digest,
            Options = options ?? Options(JobSavePolicy.Never, 50)
        };

    private HashSet<string> CollectViewNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>())
        {
            names.Add(view.Name);
        }

        return names;
    }

}