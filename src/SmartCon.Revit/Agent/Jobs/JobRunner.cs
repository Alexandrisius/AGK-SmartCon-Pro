using System.Diagnostics;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>
/// Drives bulk jobs one time slice at a time on the Revit API thread. The LLM
/// never runs the loop: it starts a job and polls its status.
/// <para>
/// Invariants: one transaction per slice (J-03), intent + undo persisted before
/// the change (J-04), independent read-back before any "done" (J-05), no
/// Thread.Sleep/.Result/.Wait on real async work (J-02).
/// </para>
/// </summary>
public sealed partial class JobRunner : IJobScheduler, IJobRecovery
{
    internal const int PreviewTtlMinutes = 30;

    private readonly IJobStore _store;
    private readonly IReadOnlyDictionary<string, IJobAction> _actions;
    private readonly ITransactionService _transactions;
    private readonly object _gate = new();

    private readonly HashSet<string> _reconciledDocuments = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, List<string>> _previewWarnings = new();

    private Guid? _activeJobId;
    private volatile bool _sliceInFlight;
    private volatile bool _wantsPump;
    private Action? _onRaise;

    public JobRunner(
        IJobStore store,
        IEnumerable<IJobAction> actions,
        ITransactionService transactions)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (actions is null) throw new ArgumentNullException(nameof(actions));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));

        var map = new Dictionary<string, IJobAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            map[action.Name] = action;
        }

        _actions = map;
    }

    /// <summary>True when a slice still has work: the pump timer uses it to keep raising.</summary>
    public bool HasWork => _wantsPump;

    public IEnumerable<string> ActionNames => _actions.Keys;

    /// <summary>Wired by the pump registration to <c>ExternalEvent.Raise()</c>.</summary>
    public void Initialize(Action onRaise)
    {
        _onRaise = onRaise ?? throw new ArgumentNullException(nameof(onRaise));
    }

    public void EnsurePump()
    {
        _onRaise?.Invoke();
    }

    public JobRecord? FindActionDriver(Guid jobId) => Sync(_store.GetAsync(jobId, CancellationToken.None));

    public JobStartResult Start(JobSpec spec, Document document)
    {
        var specError = JobSpecValidator.Validate(spec);
        if (specError is not null)
        {
            return JobStartResult.Fail(specError);
        }

        if (spec.Mode == JobMode.Preview)
        {
            // Preview never stores the large-job acknowledgement; it belongs to
            // the apply/resume call only.
            spec = WithEffectiveOptions(spec, spec.OptionsOverride, JobMode.Preview);
        }

        var documentId = AgentDocumentIdentity.GetDocumentId(document);
        if (!string.Equals(documentId, spec.DocumentId, StringComparison.Ordinal))
        {
            return JobStartResult.Fail(JobErrors.DocumentMismatch(spec.DocumentId, documentId));
        }

        if (document.IsReadOnly)
        {
            return JobStartResult.Fail(JobErrors.DocumentReadOnly());
        }

        // A workshared local copy is a normal document: ownership is taken per
        // batch and units owned by other users are skipped (JobRunner.ApplyBatch).

        if (!_actions.TryGetValue(spec.Action, out var action))
        {
            return JobStartResult.Fail(JobErrors.ActionUnknown(spec.Action));
        }

        // A job left Running by a crashed/restarted session would otherwise block
        // the new start with "already active" until the pump happens to run.
        // Only reconcile when this runner does not own a job: an in-process job
        // between slices must not be marked interrupted.
        if (_activeJobId is null)
        {
            try
            {
                Reconcile(document);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    "Agent job reconcile before start failed: " + ex.GetType().Name + ": " +
                    ex.Message + " [Action: call get_job_status; a crashed job may still block]");
            }
        }

        return spec.Mode switch
        {
            JobMode.Preview => StartPreview(spec, document, action, documentId),
            JobMode.Apply => StartApply(spec, document, action, documentId),
            JobMode.Resume => StartResume(spec, document, action, documentId),
            _ => JobStartResult.Fail(JobErrors.Spec("unsupported mode " + spec.Mode))
        };
    }

    public bool Cancel(Guid jobId, string documentId)
    {
        var job = Sync(_store.GetAsync(jobId, CancellationToken.None));
        if (job is null || !string.Equals(job.Spec.DocumentId, documentId, StringComparison.Ordinal))
        {
            return false;
        }

        if (JobStateMachine.IsTerminal(job.State))
        {
            return false;
        }

        // An interrupted job may already have applied units. With
        // rollbackOnError the cancellation must undo them, so it goes through
        // Cancelling (the pump runs the undo slice) instead of settling at once.
        if (job.State == JobState.Interrupted && job.Spec.Options.RollbackOnError)
        {
            Sync(_store.SetCancelRequestedAsync(jobId, true, CancellationToken.None));
            var rollbackError = Sync(_store.TransitionAsync(
                jobId,
                JobState.Interrupted,
                JobState.Cancelling,
                JobErrorCodes.CancelledByUser,
                JobErrors.Cancelled().ToMessage(),
                CancellationToken.None));
            if (rollbackError is not null)
            {
                return false;
            }

            _wantsPump = true;
            return true;
        }

        // Preview or an interrupted job: nothing is being applied, so the runner
        // settles the state immediately. A running job goes through Cancelling so
        // an in-flight slice stops between batches and can roll back if asked.
        if (job.State is JobState.PreviewRunning or JobState.PreviewReady
            or JobState.Interrupted or JobState.AwaitingApproval)
        {
            Sync(_store.SetCancelRequestedAsync(jobId, true, CancellationToken.None));
            var cancelled = Sync(_store.TransitionAsync(
                jobId,
                job.State,
                JobState.Cancelled,
                JobErrorCodes.CancelledByUser,
                JobErrors.Cancelled().ToMessage(),
                CancellationToken.None));

            if (cancelled is not null)
            {
                return false;
            }

            Sync(_store.MarkFinishedAsync(
                jobId,
                JobState.Cancelled,
                JobErrorCodes.CancelledByUser,
                JobErrors.Cancelled().ToMessage(),
                CancellationToken.None));
            lock (_gate)
            {
                _previewWarnings.Remove(jobId);
            }

            Sync(_store.AppendEventAsync(jobId, JobEventTypes.Cancelled, null, CancellationToken.None));
            return true;
        }

        if (job.State != JobState.Running)
        {
            return false;
        }

        // The flag is the in-flight slice's stop signal; the state transition is
        // what makes the cancel durable and lets the next slice settle it.
        Sync(_store.SetCancelRequestedAsync(jobId, true, CancellationToken.None));

        var error = Sync(_store.TransitionAsync(
            jobId,
            JobState.Running,
            JobState.Cancelling,
            null,
            null,
            CancellationToken.None));

        if (error is not null)
        {
            return false;
        }

        _wantsPump = true;
        _activeJobId = jobId;
        EnsurePump();
        return true;
    }

    /// <summary>
    /// One pump slice. Called from <c>JobPumpEventHandler.Execute</c> (Revit thread)
    /// and directly from integration tests.
    /// </summary>
    public JobSliceResult RunSlice(Document document, TimeSpan budget)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", "RunSlice"));

        lock (_gate)
        {
            if (_sliceInFlight)
            {
                return JobSliceResult.BusyResult();
            }

            _sliceInFlight = true;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var documentId = AgentDocumentIdentity.GetDocumentId(document);

            // One reconciliation per document per process: a job left Running by a
            // previous Revit session must be marked Interrupted (and resumed
            // explicitly) before any slice may apply more units to it.
            if (_reconciledDocuments.Add(documentId))
            {
                Reconcile(document);
            }

            var job = LoadActiveJob(documentId);
            if (job is null)
            {
                // A cached job in another document keeps the pump alive only while
                // that job actually needs slices (a finished preview does not).
                _wantsPump = _activeJobId is { } cachedId &&
                             JobStateMachine.NeedsPump(
                                 Sync(_store.GetAsync(cachedId, CancellationToken.None))?.State
                                 ?? JobState.Completed);
                return JobSliceResult.Idle();
            }

            if (job.State == JobState.Interrupted)
            {
                _wantsPump = false;
                return JobSliceResult.Idle();
            }

            WarnWhenStalled(job);

            var result = job.State switch
            {
                JobState.PreviewRunning => RunPreviewSlice(job, document, budget, started),
                JobState.Running => RunApplySlice(job, document, budget, started),
                JobState.Cancelling => RunCancelSlice(job, document, budget, started),
                _ => JobSliceResult.Idle()
            };

            _wantsPump = result.HasMoreWork;
            return result;
        }
        catch (Exception ex)
        {
            _wantsPump = false;
            SmartConLogger.Error(
                "Slice failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: run get_job_status; the job is marked failed to avoid a silent partial state]");

            var job = TryLoadActiveJobAfterFailure(document);
            if (job is not null)
            {
                TryFail(job, "job_slice_exception", ex.GetType().Name + ": " + ex.Message);
            }

            return JobSliceResult.Failed(new JobError(
                "job_slice_exception",
                "Job slice threw " + ex.GetType().Name + ": " + ex.Message,
                "run get_job_status and get_job_report; rollback_job restores applied units"));
        }
        finally
        {
            lock (_gate)
            {
                _sliceInFlight = false;
            }
        }
    }

    /// <summary>
    /// Runs slices until the job reaches a state that needs no pump. Used by
    /// integration tests and by the inline first slice of <see cref="Start"/>.
    /// </summary>
    public JobSliceResult RunToCompletion(Document document, TimeSpan? maxDuration = null)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", "RunToCompletion"));

        var limit = maxDuration ?? TimeSpan.FromMinutes(10);
        var trackedJobId = _activeJobId;
        var totalStarted = Stopwatch.GetTimestamp();
        var processed = 0;
        JobSliceResult last = JobSliceResult.Idle();

        while (true)
        {
            var slice = RunSlice(document, TimeSpan.FromMilliseconds(200));
            processed += slice.ProcessedThisSlice;
            if (slice.JobId is not null)
            {
                last = slice;
            }

            if (slice.Busy || !slice.HasMoreWork || slice.Error is not null)
            {
                break;
            }

            var elapsed = Stopwatch.GetTimestamp() - totalStarted;
            if (elapsed * 1000.0 / Stopwatch.Frequency > limit.TotalMilliseconds)
            {
                SmartConLogger.Warn(
                    "RunToCompletion exceeded " +
                    limit.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                    "s [Action: the job continues when the pump runs again; check get_job_status]");
                break;
            }
        }

        // The trailing slice may be Idle (the job already finished inside Start);
        // report the stored state so callers never see a null state for a live job.
        var resolvedJobId = last.JobId ?? trackedJobId;
        if (resolvedJobId is { } jobId)
        {
            var record = Sync(_store.GetAsync(jobId, CancellationToken.None));
            if (record is not null)
            {
                return new JobSliceResult
                {
                    JobId = jobId,
                    State = record.State,
                    HasMoreWork = JobStateMachine.NeedsPump(record.State),
                    ProcessedThisSlice = processed,
                    Error = last.Error
                };
            }
        }

        return new JobSliceResult
        {
            JobId = last.JobId,
            State = last.State,
            HasMoreWork = last.HasMoreWork,
            ProcessedThisSlice = processed,
            Error = last.Error
        };
    }

    /// <summary>Reconciliation entry point for a document that was just opened or re-activated.</summary>
    public IReadOnlyList<Guid> Reconcile(Document document)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", "Reconcile"));

        var documentId = AgentDocumentIdentity.GetDocumentId(document);
        var active = Sync(_store.GetActiveAsync(documentId, CancellationToken.None));
        if (active is null)
        {
            return Array.Empty<Guid>();
        }

        if (_activeJobId == active.JobId && _sliceInFlight)
        {
            return Array.Empty<Guid>();
        }

        if (active.State is JobState.Running or JobState.PreviewRunning or JobState.Cancelling)
        {
            var error = Sync(_store.TransitionAsync(
                active.JobId,
                active.State,
                JobState.Interrupted,
                JobErrorCodes.Interrupted,
                "The process stopped before the job finished",
                CancellationToken.None));

            if (error is null)
            {
                Sync(_store.AppendEventAsync(
                    active.JobId,
                    JobEventTypes.Interrupted,
                    "{\"fromState\":" + (int)active.State + "}",
                    CancellationToken.None));
                SmartConLogger.Warn(
                    "Job " + active.JobId.ToString("D") + " (" + active.Spec.Action +
                    ") was interrupted; " + active.LastSavedCursor + " units are durable " +
                    "[Action: call start_job with resumeJobId to continue]");
                return new[] { active.JobId };
            }
        }

        return Array.Empty<Guid>();
    }

    private JobRecord? LoadActiveJob(string documentId)
    {
        if (_activeJobId is { } id)
        {
            var job = Sync(_store.GetAsync(id, CancellationToken.None));
            if (job is not null && !JobStateMachine.IsTerminal(job.State))
            {
                // Another document is active: the job is alive but cannot run here.
                // Keep it cached so the pump resumes when the document comes back.
                return string.Equals(job.Spec.DocumentId, documentId, StringComparison.Ordinal)
                    ? job
                    : null;
            }

            _activeJobId = null;
        }

        var active = Sync(_store.GetActiveAsync(documentId, CancellationToken.None));
        if (active is not null)
        {
            _activeJobId = active.JobId;
        }

        return active;
    }

    private JobRecord? TryLoadActiveJobAfterFailure(Document document)
    {
        try
        {
            var documentId = AgentDocumentIdentity.GetDocumentId(document);
            return Sync(_store.GetActiveAsync(documentId, CancellationToken.None));
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Could not reload the active job after a slice failure: " +
                ex.GetType().Name + " [Action: run get_job_status to inspect the stored state]");
            return null;
        }
    }
}

public sealed class JobSliceResult
{
    public Guid? JobId { get; init; }

    public JobState? State { get; init; }

    public bool HasMoreWork { get; init; }

    public bool Busy { get; init; }

    public int ProcessedThisSlice { get; init; }

    public JobError? Error { get; init; }

    public static JobSliceResult Idle() => new();

    public static JobSliceResult BusyResult() => new() { Busy = true };

    public static JobSliceResult Failed(JobError error) => new() { Error = error };
}
