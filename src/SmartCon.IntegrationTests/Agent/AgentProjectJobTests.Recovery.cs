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

public sealed partial class AgentProjectJobTests
{
    [Test]
    public async Task Job_PreviewStale_RefusesToOverwriteNewerChange()
    {
        var preview = Runner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 50)),
            Doc);
        await Assert.That(preview.Success).IsTrue();

        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);

        // A user renames one view after the preview: that unit must not be
        // overwritten by the frozen plan.
        var target = new FilteredElementCollector(Doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .First(view => view.Name == ViewPrefix + "1");
        var staleUniqueId = target.UniqueId;
        _transactions!.RunInTransaction(Doc, "External rename", _ => target.Name = "SC_Job_External_1");

        var digest = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!.Digest;
        Runner.Start(
            RenameViewSpec(JobMode.Apply, digest, Options(JobSavePolicy.Never, 50)),
            Doc);
        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        await Assert.That(result.State).IsEqualTo(JobState.CompletedWithFailures);

        var items = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        var stale = items.Single(item => item.UnitKey == staleUniqueId);
        await Assert.That(stale.State).IsEqualTo(JobItemState.Failed);
        await Assert.That(stale.ErrorCode).IsEqualTo(JobErrorCodes.PreviewStale);

        var externallyRenamed = Doc.GetElement(staleUniqueId) as View;
        await Assert.That(externallyRenamed!.Name).IsEqualTo("SC_Job_External_1");
    }

    [Test]
    public async Task Job_ReportPagination_RespectsOffset()
    {
        var job = PrepareApplyJob(JobSavePolicy.Never, batchSize: 50);
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        var first = new JobReportBuilder(Store, _actions!)
            .Build(Doc, job.JobId, offset: 0, limit: 100, readBack: false, out var firstError);
        var second = new JobReportBuilder(Store, _actions!)
            .Build(Doc, job.JobId, offset: 100, limit: 50, readBack: false, out var secondError);

        await Assert.That(firstError).IsNull();
        await Assert.That(secondError).IsNull();
        await Assert.That(first!.Items.Count).IsEqualTo(100);
        await Assert.That(first.NextOffset).IsEqualTo(100);
        await Assert.That(second!.Items.Count).IsEqualTo(50);
        await Assert.That(second.Items[0].Seq).IsEqualTo(101);
        await Assert.That(second.NextOffset).IsEqualTo(150);
    }

    [Test]
    public async Task Job_ReportForForeignDocument_IsRejected()
    {
        var job = PrepareApplyJob(JobSavePolicy.Never, batchSize: 50);
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        var other = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var report = new JobReportBuilder(Store, _actions!)
                .Build(other, job.JobId, offset: 0, limit: 10, readBack: true, out var error);

            await Assert.That(report).IsNull();
            await Assert.That(error).IsNotNull();
            await Assert.That(error!.Code).IsEqualTo(JobErrorCodes.DocumentMismatch);
        }
        finally
        {
            other.Close(false);
        }
    }

    [Test]
    public async Task Job_FirstSliceAfterRestart_MarksCrashedJobInterrupted()
    {
        var slowRunner = CreateRunner(new SlowAction(new RenameViewsJobAction(), 1));
        var preview = slowRunner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 50)),
            Doc);
        await Assert.That(preview.Success).IsTrue();

        var jobId = preview.Job!.JobId;
        slowRunner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        var record = await Store.GetAsync(jobId, CancellationToken.None);
        var digest = JobJson.Deserialize<JobPreview>(record!.PreviewJson)!.Digest;

        slowRunner.Start(
            RenameViewSpec(JobMode.Apply, digest, Options(JobSavePolicy.Never, 50)),
            Doc);
        slowRunner.RunSlice(Doc, TimeSpan.FromMilliseconds(200));

        var doneBefore = (await Store.LoadAllItemsAsync(jobId, CancellationToken.None))
            .Count(item => item.State == JobItemState.Done);

        // A brand-new runner (simulated process restart) must reconcile on its
        // first slice, without an explicit Reconcile call.
        var restarted = CreateRunner(new RenameViewsJobAction());
        restarted.RunSlice(Doc, TimeSpan.FromMilliseconds(200));

        var after = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(after!.State).IsEqualTo(JobState.Interrupted);

        var doneAfter = (await Store.LoadAllItemsAsync(jobId, CancellationToken.None))
            .Count(item => item.State == JobItemState.Done);
        await Assert.That(doneAfter).IsEqualTo(doneBefore);
    }

    [Test]
    public async Task Job_RetryFailed_FromCompletedWithFailures_TransitionsAndRetries()
    {
        var preview = Runner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 50)),
            Doc);
        await Assert.That(preview.Success).IsTrue();

        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);

        // Force one unit to fail honestly: the model changes after the preview.
        var target = new FilteredElementCollector(Doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .First(view => view.Name == ViewPrefix + "2");
        _transactions!.RunInTransaction(Doc, "External rename", _ => target.Name = "SC_Job_External_2");

        var digest = JobJson.Deserialize<JobPreview>(previewRecord!.PreviewJson)!.Digest;
        Runner.Start(
            RenameViewSpec(JobMode.Apply, digest, Options(JobSavePolicy.Never, 50)),
            Doc);
        var applied = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        await Assert.That(applied.State).IsEqualTo(JobState.CompletedWithFailures);

        var retry = Runner.RetryFailed(Doc, jobId);

        await Assert.That(retry.ErrorCode).IsNull();
        await Assert.That(retry.Reset).IsEqualTo(1);
        await Assert.That(retry.State).IsNotEqualTo(JobState.Failed);

        var after = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(after!.State).IsEqualTo(JobState.Completed);

        var items = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(ViewCount);
        await Assert.That(items.Count(item => item.State == JobItemState.Failed)).IsEqualTo(0);
    }


    [Test]
    public async Task Job_RollbackContinuesAfterAnInterruptedUndoPass()
    {
        var job = PrepareApplyJob(JobSavePolicy.Never, batchSize: 50);
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        // Simulate a rollback that was killed mid-pass: every unit is left Undoing.
        var items = await Store.LoadAllItemsAsync(job.JobId, CancellationToken.None);
        await Store.ResetItemsAsync(
            job.JobId,
            items.Select(item => item.Seq).ToList(),
            JobItemState.Undoing,
            CancellationToken.None);

        var rollback = Runner.Rollback(Doc, job.JobId, dryRun: false);

        await Assert.That(rollback.ErrorCode).IsNull();
        await Assert.That(rollback.Restored).IsEqualTo(ViewCount);
        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);

        var names = CollectViewNames();
        await Assert.That(names.Count(name => name.StartsWith(ViewPrefix))).IsEqualTo(ViewCount);
    }

    /// <summary>Delegates to the real action and fires a callback once a seq is reached.</summary>
    private sealed class CancelAfterAction : IJobAction
    {
        private readonly IJobAction _inner;
        private readonly int _atSeq;
        private readonly Action<Guid> _onReached;

        public CancelAfterAction(IJobAction inner, int atSeq, Action<Guid> onReached)
        {
            _inner = inner;
            _atSeq = atSeq;
            _onReached = onReached;
        }

        public string Name => _inner.Name;

        public JobActionScope Scope => _inner.Scope;

        public int MaxUnits => _inner.MaxUnits;

        public JobExpansion Expand(JobActionContext context, string specJson) =>
            _inner.Expand(context, specJson);

        public JobUnitPreview Preview(JobActionContext context, JobUnit unit) =>
            _inner.Preview(context, unit);

        public JobUnitApply Apply(
            JobActionContext context,
            JobUnit unit,
            JobUnitPreview preview,
            int attempt,
            Action<string>? persistUndo)
        {
            var result = _inner.Apply(context, unit, preview, attempt, persistUndo);
            if (unit.Seq >= _atSeq)
            {
                _onReached(context.JobId);
            }

            return result;
        }

        public JobUnitVerification Verify(
            JobActionContext context,
            JobUnit unit,
            JobUnitPreview preview) =>
            _inner.Verify(context, unit, preview);

        public JobUnitUndo Undo(
            JobActionContext context,
            JobUnit unit,
            string? undoJson,
            bool dryRun) =>
            _inner.Undo(context, unit, undoJson, dryRun);
    }

    /// <summary>Delegates to the real action with a ~1 ms artificial cost per unit.</summary>
    private sealed class SlowAction : IJobAction
    {
        private readonly IJobAction _inner;
        private readonly int _milliseconds;

        public SlowAction(IJobAction inner, int milliseconds)
        {
            _inner = inner;
            _milliseconds = milliseconds;
        }

        public string Name => _inner.Name;

        public JobActionScope Scope => _inner.Scope;

        public int MaxUnits => _inner.MaxUnits;

        public JobExpansion Expand(JobActionContext context, string specJson) =>
            _inner.Expand(context, specJson);

        public JobUnitPreview Preview(JobActionContext context, JobUnit unit) =>
            _inner.Preview(context, unit);

        public JobUnitApply Apply(
            JobActionContext context,
            JobUnit unit,
            JobUnitPreview preview,
            int attempt,
            Action<string>? persistUndo)
        {
            Spin(_milliseconds);
            return _inner.Apply(context, unit, preview, attempt, persistUndo);
        }

        public JobUnitVerification Verify(
            JobActionContext context,
            JobUnit unit,
            JobUnitPreview preview) =>
            _inner.Verify(context, unit, preview);

        public JobUnitUndo Undo(
            JobActionContext context,
            JobUnit unit,
            string? undoJson,
            bool dryRun) =>
            _inner.Undo(context, unit, undoJson, dryRun);

        private static void Spin(int milliseconds)
        {
            var until = Stopwatch.GetTimestamp() +
                        (long)(Stopwatch.Frequency * (milliseconds / 1000.0));
            while (Stopwatch.GetTimestamp() < until)
            {
            }
        }
    }
}
