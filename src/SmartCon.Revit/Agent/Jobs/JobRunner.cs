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

    private Guid? _activeJobId;
    private bool _sliceInFlight;
    private bool _wantsPump;
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

        var documentId = AgentDocumentIdentity.GetDocumentId(document);
        if (!string.Equals(documentId, spec.DocumentId, StringComparison.Ordinal))
        {
            return JobStartResult.Fail(JobErrors.DocumentMismatch(spec.DocumentId, documentId));
        }

        if (document.IsWorkshared)
        {
            return JobStartResult.Fail(JobErrors.Workshared());
        }

        if (document.IsReadOnly)
        {
            return JobStartResult.Fail(JobErrors.DocumentReadOnly());
        }

        if (!_actions.TryGetValue(spec.Action, out var action))
        {
            return JobStartResult.Fail(JobErrors.ActionUnknown(spec.Action));
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

        Sync(_store.SetCancelRequestedAsync(jobId, true, CancellationToken.None));

        // Preview or an interrupted job: nothing is being applied, so the runner
        // can settle the state immediately. A running job goes through Cancelling
        // so the pump stops between units and can roll back if asked.
        if (job.State is JobState.PreviewRunning or JobState.PreviewReady
            or JobState.Interrupted or JobState.AwaitingApproval)
        {
            var target = job.State == JobState.PreviewRunning
                ? JobState.Cancelled
                : JobState.Cancelled;

            var error = Sync(_store.TransitionAsync(
                jobId,
                job.State,
                target,
                JobErrorCodes.CancelledByUser,
                JobErrors.Cancelled().ToMessage(),
                CancellationToken.None));

            if (error is null)
            {
                Sync(_store.MarkFinishedAsync(
                    jobId,
                    target,
                    JobErrorCodes.CancelledByUser,
                    JobErrors.Cancelled().ToMessage(),
                    CancellationToken.None));
                Sync(_store.AppendEventAsync(jobId, JobEventTypes.Cancelled, null, CancellationToken.None));
                return true;
            }
        }
        else if (job.State == JobState.Running)
        {
            var error = Sync(_store.TransitionAsync(
                jobId,
                JobState.Running,
                JobState.Cancelling,
                null,
                null,
                CancellationToken.None));
            if (error is null)
            {
                _wantsPump = true;
                _activeJobId = jobId;
                EnsurePump();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One pump slice. Called from <c>JobPumpEventHandler.Execute</c> (Revit thread)
    /// and directly from integration tests.
    /// </summary>
    public JobSliceResult RunSlice(Document document, TimeSpan budget)
    {
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
            var job = LoadActiveJob(documentId);
            if (job is null)
            {
                _wantsPump = false;
                return JobSliceResult.Idle();
            }

            if (!string.Equals(job.Spec.DocumentId, documentId, StringComparison.Ordinal))
            {
                _wantsPump = false;
                return JobSliceResult.Failed(JobErrors.DocumentMismatch(job.Spec.DocumentId, documentId));
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
                "[AgentJob] Slice failed: " + ex.GetType().Name + ": " + ex.Message +
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
                    "[AgentJob] RunToCompletion exceeded " +
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
                    "[AgentJob] Job " + active.JobId.ToString("D") + " (" + active.Spec.Action +
                    ") was interrupted; " + active.LastSavedCursor + " units are durable " +
                    "[Action: call start_job with mode=resume and the jobId to continue]");
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
                return job;
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
                "[AgentJob] Could not reload the active job after a slice failure: " +
                ex.GetType().Name + " [Action: run get_job_status to inspect the stored state]");
            return null;
        }
    }

    private JobActionContext CreateContext(
        Document document,
        string documentId,
        Guid jobId,
        JobSpec spec,
        bool dryRun) =>
        new()
        {
            Document = document,
            DocumentId = documentId,
            JobId = jobId,
            DryRun = dryRun,
            SpecJson = spec.SpecJson,
            CancellationToken = CancellationToken.None
        };

    private static IReadOnlyList<JobUnit> Normalize(IReadOnlyList<JobUnit> units)
    {
        var result = new List<JobUnit>(units.Count);
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            result.Add(new JobUnit
            {
                Seq = i + 1,
                UnitKey = unit.UnitKey,
                ArgsJson = unit.ArgsJson,
                Label = unit.Label
            });
        }

        return result;
    }

    private JobError? PreflightSaveable(Document document, JobOptions options)
    {
        if (!JobSavePolicyDecider.RequiresSaveableDocument(options.SavePolicy))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(document.PathName))
        {
            return JobErrors.DocumentNotSaved();
        }

        if (document.IsReadOnlyFile)
        {
            return JobErrors.DocumentReadOnly();
        }

        return null;
    }

    private void WarnWhenStalled(JobRecord job)
    {
        if (!JobStateMachine.NeedsPump(job.State))
        {
            return;
        }

        var idleSeconds = (DateTime.UtcNow - job.UpdatedAt).TotalSeconds;
        if (idleSeconds > 30)
        {
            SmartConLogger.Warn(
                "[AgentJob] Job " + job.JobId.ToString("D") + " has not advanced for " +
                idleSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                "s (state " + job.State + ") " +
                "[Action: check that Revit has focus; the pump resumes on the next idle cycle, " +
                "or cancel the job with cancel_job]");
        }
    }

    private void TryFail(JobRecord job, string code, string detail)
    {
        try
        {
            Sync(_store.MarkFinishedAsync(job.JobId, JobState.Failed, code, detail, CancellationToken.None));
            Sync(_store.AppendEventAsync(job.JobId, JobEventTypes.Failed, null, CancellationToken.None));
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                "[AgentJob] Could not mark job " + job.JobId.ToString("D") + " as failed: " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: inspect agent.db jobs table and cancel the job manually]");
        }
    }

    internal static JobPreview? ParsePreview(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JobJson.Deserialize<JobPreview>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Store calls complete synchronously in the SQLite implementation, and the
    /// runner is only ever called on the Revit API thread.
    /// </summary>
    private static T Sync<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Sync(Task task) => task.GetAwaiter().GetResult();
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