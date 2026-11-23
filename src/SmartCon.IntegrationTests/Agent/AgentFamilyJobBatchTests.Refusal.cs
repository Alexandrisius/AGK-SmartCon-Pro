using System.Diagnostics;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Document refusals of family jobs (review r1 of the jobs package): the runner
/// stops the job with one honest error and the stored record describes the model
/// as the job leaves it — with and without rollbackOnError, with the synthetic
/// double and with the real family action.
/// </summary>
public sealed partial class AgentFamilyJobBatchTests
{
    private const double Type1HeightFeet = 2000.0 / 304.8;

    // No field initializer with a Revit type argument: it would load RevitAPI before the injector is ready.
    private List<Document>? _openFamilyDocs;

    private List<Document> OpenFamilyDocs => _openFamilyDocs ??= new();

    /// <summary>
    /// rollbackOnError=true and a refusal in apply: the units applied before the
    /// refusal are undone BEFORE the job is finalized, so the record says
    /// RolledBack and never claims that a unit stays applied.
    /// </summary>
    [Test]
    public async Task Runner_DocumentRefusalInApply_WithRollbackOnError()
    {
        var databasePath = NewDatabase(out var store);
        var jobId = StartRefusingPreview(store, refuseAtSeq: 99);

        var applyRunner = NewRunner(store, new RefusingJobAction { UnitCount = 4, RefuseAtSeq = 2 });
        var apply = applyRunner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "refusing_probe",
                Mode = JobMode.Apply,
                SpecJson = "{}",
                PreviewDigest = await DigestOfAsync(store, jobId),
                Options = Options(),
                // Apply merges the override onto the preview's options; Options alone is ignored.
                OptionsOverride = new JobOptionsOverride { RollbackOnError = true }
            },
            Doc);
        await Assert.That(apply.Success).IsTrue();
        var result = applyRunner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));

        await Assert.That(result.State).IsEqualTo(JobState.RolledBack);
        await Assert.That(result.Error!.Code).IsEqualTo(JobErrorCodes.DocumentReadOnly);
        await Assert.That(result.Error.Message.Contains("2 unit(s) were not attempted", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Error.Message.Contains("stay applied", StringComparison.Ordinal)).IsFalse();

        var job = await store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(job!.State).IsEqualTo(JobState.RolledBack);
        await Assert.That(job.ErrorCode).IsEqualTo(JobErrorCodes.DocumentReadOnly);
        await Assert.That(job.ErrorDetail!.Contains("Rolled back 1 unit(s)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(job.ErrorDetail.Contains("stay applied", StringComparison.Ordinal)).IsFalse();

        var items = await store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(items.Single(item => item.Seq == 1).State).IsEqualTo(JobItemState.Undone);
        await Assert.That(items.Single(item => item.Seq == 2).ErrorCode).IsEqualTo(RefusingJobAction.RefusalCode);
        await Assert.That(items.Single(item => item.Seq == 3).State).IsEqualTo(JobItemState.Previewed);
        await Assert.That(items.Single(item => item.Seq == 4).State).IsEqualTo(JobItemState.Previewed);

        var totals = JobJson.Deserialize<JobTotals>(job.TotalsJson)!;
        await Assert.That(totals.Done).IsEqualTo(0);
        await Assert.That(totals.Undone).IsEqualTo(1);
        await Assert.That(totals.Failed).IsEqualTo(1);

        var events = await EventTypesAsync(store, jobId);
        await Assert.That(events.IndexOf(JobEventTypes.RollbackStarted)).IsGreaterThanOrEqualTo(0);
        await Assert.That(events.IndexOf(JobEventTypes.RollbackCompleted)).IsGreaterThan(events.IndexOf(JobEventTypes.RollbackStarted));
        await Assert.That(events.Contains(JobEventTypes.Failed)).IsFalse();

        TryDeleteDatabase(databasePath);
    }

    /// <summary>
    /// The real refusal: the project is inside another transaction, so Revit
    /// refuses to open a family of it for editing. The family action waits a
    /// bounded time for the project to accept edits again before it reports the
    /// refusal, and the job stops on it with one error. The advised rerun
    /// succeeds once the document accepts edits again.
    /// </summary>
    [Test]
    public async Task Runner_FamilyAction_ProjectBusyInPreview_WaitsThenRefuses()
    {
        if (!SeedFamilies())
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        var databasePath = NewDatabase(out var store);
        var runner = NewRunner(store, new MapFamilyParametersJobAction());
        using var blocker = BlockProjectEdits();

        var watch = Stopwatch.StartNew();
        var start = runner.Start(PreviewSpec(DocumentId, MapSpec), Doc);
        var result = runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        watch.Stop();

        await Assert.That(result.State).IsEqualTo(JobState.Failed);
        var jobId = start.Job!.JobId;
        var job = await store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(job!.ErrorCode).IsEqualTo(JobErrorCodes.DocumentReadOnly);
        await Assert.That(job.ErrorDetail!.Contains("was being previewed", StringComparison.Ordinal)).IsTrue();
        await Assert.That(job.ErrorDetail.Contains("Nothing was applied; 2 unit(s) were not attempted", StringComparison.Ordinal)).IsTrue();
        await Assert.That(job.ErrorDetail.Contains("resume with start_job", StringComparison.Ordinal)).IsFalse();

        var items = await store.LoadAllItemsAsync(jobId, CancellationToken.None);
        var refused = items.Single(item => item.Seq == 1);
        await Assert.That(refused.State).IsEqualTo(JobItemState.Failed);
        await Assert.That(refused.ErrorCode).IsEqualTo(AgentFamilySession.PermissionDenied);
        await Assert.That(refused.ErrorDetail!.Contains("being modified by another transaction", StringComparison.Ordinal)).IsTrue();
        await Assert.That(items.Single(item => item.Seq == 2).State).IsEqualTo(JobItemState.Pending);
        await Assert.That(items.Single(item => item.Seq == 3).State).IsEqualTo(JobItemState.Pending);

        // The bounded wait is what makes the refusal honest instead of a lost race.
        await Assert.That(watch.ElapsedMilliseconds).IsGreaterThanOrEqualTo(950);

        blocker.RollBack();
        runner.Start(PreviewSpec(DocumentId, MapSpec), Doc);
        var rerun = runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        await Assert.That(rerun.State).IsEqualTo(JobState.PreviewReady);

        TryDeleteDatabase(databasePath);
    }

    /// <summary>
    /// The real refusal while applying: the first unit is applied, then another
    /// transaction opens on the project before the next slice. The applied unit
    /// stays applied, the record says so, and the advised rollback_job restores
    /// it once the document accepts edits again.
    /// </summary>
    [Test]
    public async Task Runner_FamilyAction_ProjectBusyInApply_KeepsAppliedUnitUntilRollback()
    {
        if (!SeedFamilies())
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        var databasePath = NewDatabase(out var store);
        var runner = NewRunner(store, new MapFamilyParametersJobAction());
        var start = runner.Start(PreviewSpec(DocumentId, MapSpec), Doc);
        runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var job = await store.GetAsync(start.Job!.JobId, CancellationToken.None);
        await Assert.That(job!.State).IsEqualTo(JobState.PreviewReady);
        var jobId = job.JobId;

        // The first slice of the apply call applies the first unit only.
        var apply = runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "map_family_parameters",
                Mode = JobMode.Apply,
                SpecJson = MapSpec,
                PreviewDigest = JobJson.Deserialize<JobPreview>(job.PreviewJson)!.Digest,
                Options = Options()
            },
            Doc);
        await Assert.That(apply.Success).IsTrue();
        await Assert.That(apply.Job!.State).IsEqualTo(JobState.Running);

        using var blocker = BlockProjectEdits();

        var watch = Stopwatch.StartNew();
        var slice = runner.RunSlice(Doc, TimeSpan.FromMilliseconds(1500));
        watch.Stop();

        await Assert.That(slice.State).IsEqualTo(JobState.Failed);
        await Assert.That(slice.Error!.Code).IsEqualTo(JobErrorCodes.DocumentReadOnly);
        await Assert.That(watch.ElapsedMilliseconds).IsGreaterThanOrEqualTo(950);

        job = await store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(job!.State).IsEqualTo(JobState.Failed);
        await Assert.That(job.ErrorCode).IsEqualTo(JobErrorCodes.DocumentReadOnly);
        await Assert.That(job.ErrorDetail!.Contains("1 unit(s) applied before it stay applied; 1 unit(s) were not attempted", StringComparison.Ordinal)).IsTrue();

        var items = await store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(items.Single(item => item.Seq == 1).State).IsEqualTo(JobItemState.Done);
        var refused = items.Single(item => item.Seq == 2);
        await Assert.That(refused.State).IsEqualTo(JobItemState.Failed);
        await Assert.That(refused.ErrorCode).IsEqualTo(AgentFamilySession.PermissionDenied);
        await Assert.That(items.Single(item => item.Seq == 3).State).IsEqualTo(JobItemState.Previewed);

        // The record matches the model: the family of the applied unit carries the mapping.
        var appliedFamily = items.Single(item => item.Seq == 1).Label!;
        var seed = _seeds.Single(candidate => string.Equals(candidate.FamilyName, appliedFamily, StringComparison.OrdinalIgnoreCase));
        var mapped = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.HeightParam);
        await Assert.That(Math.Abs(mapped!.AsDouble() - FamilySeed.Type1WidthMm / 304.8) < 1e-9).IsTrue();

        var events = await EventTypesAsync(store, jobId);
        await Assert.That(events.Contains(JobEventTypes.Failed)).IsTrue();
        await Assert.That(events.Contains(JobEventTypes.RollbackStarted)).IsFalse();

        blocker.RollBack();

        var rollback = runner.Rollback(Doc, jobId, dryRun: false);
        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);

        items = await store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(items.Single(item => item.Seq == 1).State).IsEqualTo(JobItemState.Undone);
        var restored = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.HeightParam);
        await Assert.That(Math.Abs(restored!.AsDouble() - Type1HeightFeet) < 1e-9).IsTrue();
        await Assert.That(Doc.Application.Documents.Cast<Document>().Count()).IsEqualTo(1);

        TryDeleteDatabase(databasePath);
    }

    /// <summary>
    /// The host dispatches a call that was raised off the Revit thread to the
    /// Revit thread; it never runs inline on the caller (Revit reports an
    /// untouched document as read-only from another thread).
    /// </summary>
    [Test]
    public async Task Host_CallRaisedOffTheRevitThread_RunsOnTheRevitThread()
    {
        using var host = CreateHost();
        var revitThread = Environment.CurrentManagedThreadId;

        var ranOn = Pump(() => host.RevitEvent.RaiseAsync(
            _ => Environment.CurrentManagedThreadId,
            CancellationToken.None));

        await Assert.That(ranOn).IsEqualTo(revitThread);
    }

    /// <summary>
    /// A family document that survives Close(false) is reported as such: the
    /// close section says failed, and the document is still open.
    /// </summary>
    [Test]
    public async Task FamilySession_CloseRefused_ReportsFailedSection()
    {
        if (!SeedFamilies())
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        var session = AgentFamilySession.Open(Doc, FindFamily(FamilyPrefix + "1"), new WriteOutcome());
        await Assert.That(session).IsNotNull();
        OpenFamilyDocs.Add(session!.FamilyDoc);

        // Revit refuses to close a document while a transaction is open in it.
        var transaction = new Transaction(session.FamilyDoc, "Block close");
        transaction.Start();
        try
        {
            session.Dispose();

            var close = session.Sections.Last();
            await Assert.That(close.Section).IsEqualTo("close");
            await Assert.That(close.Status).IsEqualTo("failed");
            await Assert.That(close.Reason!.Contains("still open after Close(false)", StringComparison.Ordinal)).IsTrue();
            await Assert.That(session.FamilyDoc.IsValidObject).IsTrue();
        }
        finally
        {
            try
            {
                if (transaction.HasStarted())
                {
                    transaction.RollBack();
                }
            }
            catch (Exception)
            {
                // The assertion above already failed: Revit closed the document despite the open transaction.
            }
        }

        await Assert.That(AgentFamilyDocCloser.Close(session.FamilyDoc)).IsTrue();
    }

    private JobRunner NewRunner(SqliteAgentJobStore store, IJobAction action) => new(
        store,
        new IJobAction[] { action },
        new RevitTransactionService(new StubRevitContext(Doc)));

    private Guid StartRefusingPreview(SqliteAgentJobStore store, int refuseAtSeq)
    {
        var previewRunner = NewRunner(store, new RefusingJobAction { UnitCount = 4, RefuseAtSeq = refuseAtSeq });
        var start = previewRunner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "refusing_probe",
                Mode = JobMode.Preview,
                SpecJson = "{}",
                Options = Options()
            },
            Doc);
        previewRunner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        return start.Job!.JobId;
    }

    private static async Task<string> DigestOfAsync(SqliteAgentJobStore store, Guid jobId) =>
        JobJson.Deserialize<JobPreview>(
            (await store.GetAsync(jobId, CancellationToken.None))!.PreviewJson)!.Digest;

    private static async Task<List<string>> EventTypesAsync(SqliteAgentJobStore store, Guid jobId) =>
        (await store.LoadEventsAsync(jobId, 50, CancellationToken.None))
        .Select(jobEvent => jobEvent.Type)
        .ToList();

    /// <summary>
    /// Opens a transaction on the project and keeps it open: Revit then refuses
    /// to open a family of that project for editing.
    /// </summary>
    private ITransactionScope BlockProjectEdits() =>
        new RevitTransactionService(new StubRevitContext(Doc)).BeginTransaction(Doc, "Block family edits");

    private RevitFamily FindFamily(string familyName)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily candidate &&
                string.Equals(candidate.Name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Seed family '" + familyName + "' was not found");
    }
}
