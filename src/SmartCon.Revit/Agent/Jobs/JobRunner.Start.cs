using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>Start / preview / apply / resume entry points of the job runner.</summary>
public sealed partial class JobRunner
{
    private JobStartResult StartPreview(
        JobSpec spec,
        Document document,
        IJobAction action,
        string documentId)
    {
        var active = Sync(_store.GetActiveAsync(documentId, CancellationToken.None));
        if (active is not null)
        {
            return JobStartResult.Fail(JobErrors.AlreadyActive(active.JobId.ToString("D")));
        }

        var saveError = PreflightSaveable(document, spec.Options);
        if (saveError is not null)
        {
            return JobStartResult.Fail(saveError);
        }

        var record = Sync(_store.CreateAsync(spec, CancellationToken.None));
        var context = CreateContext(document, documentId, record.JobId, spec, dryRun: true);

        JobExpansion expansion;
        try
        {
            expansion = action.Expand(context, spec.SpecJson);
        }
        catch (Exception ex)
        {
            TryFail(record, JobErrorCodes.InvalidSpec, ex.GetType().Name + ": " + ex.Message);
            return JobStartResult.Fail(new JobError(
                JobErrorCodes.InvalidSpec,
                "Action '" + action.Name + "' failed to expand the selector: " + ex.Message,
                "fix the spec arguments and call start_job again"));
        }

        if (!expansion.Success)
        {
            var error = new JobError(
                expansion.ErrorCode!,
                expansion.ErrorDetail ?? "Expansion failed",
                "fix the spec arguments and call start_job again");
            TryFail(record, error.Code, error.Message);
            return JobStartResult.Fail(error);
        }

        if (expansion.Units.Count == 0)
        {
            var error = JobErrors.SelectorEmpty(action.Name);
            TryFail(record, error.Code, error.Message);
            return JobStartResult.Fail(error);
        }

        var limit = Math.Min(action.MaxUnits, spec.Options.MaxItems);
        if (expansion.Units.Count > limit)
        {
            var error = JobErrors.LimitExceeded(expansion.Units.Count, limit);
            TryFail(record, error.Code, error.Message);
            return JobStartResult.Fail(error);
        }

        lock (_gate)
        {
            _previewWarnings[record.JobId] = expansion.Warnings.ToList();
        }

        Sync(_store.AddUnitsAsync(record.JobId, Normalize(expansion.Units), CancellationToken.None));
        Sync(_store.AppendEventAsync(
            record.JobId,
            JobEventTypes.PreviewStarted,
            "{\"units\":" + expansion.Units.Count + "}",
            CancellationToken.None));

        _activeJobId = record.JobId;
        _wantsPump = true;

        RunSlice(document, TimeSpan.FromMilliseconds(spec.Options.SliceBudgetMs));

        var updated = Sync(_store.GetAsync(record.JobId, CancellationToken.None)) ?? record;
        _wantsPump = updated.State == JobState.PreviewRunning;
        return JobStartResult.Ok(updated);
    }

    private JobStartResult StartApply(
        JobSpec spec,
        Document document,
        IJobAction action,
        string documentId)
    {
        JobRecord? job = null;
        if (spec.ResumeJobId is { } explicitId)
        {
            job = Sync(_store.GetAsync(explicitId, CancellationToken.None));
        }

        if (job is null && !string.IsNullOrWhiteSpace(spec.PreviewDigest))
        {
            job = Sync(_store.GetByPreviewDigestAsync(documentId, spec.PreviewDigest!, CancellationToken.None));
        }

        if (job is null)
        {
            return JobStartResult.Fail(JobErrors.Preview(
                "no job matches the provided previewDigest",
                "run start_job with mode=preview first and apply with the returned previewDigest"));
        }

        if (!string.Equals(job.Spec.Action, spec.Action, StringComparison.OrdinalIgnoreCase))
        {
            return JobStartResult.Fail(JobErrors.PreviewMismatch());
        }

        if (job.State != JobState.PreviewReady)
        {
            return JobStartResult.Fail(JobErrors.InvalidState(
                "apply requires a job in state PreviewReady, but it is '" + job.State + "'",
                "run start_job with mode=preview again and apply the fresh previewDigest"));
        }

        var preview = ParsePreview(job.PreviewJson);
        if (preview is null)
        {
            return JobStartResult.Fail(JobErrors.Preview(
                "the stored preview is unreadable",
                "run start_job with mode=preview again"));
        }

        var envelope = JobSpecValidator.ValidateApplyEnvelope(preview, spec, DateTime.UtcNow);
        if (envelope is not null)
        {
            return JobStartResult.Fail(envelope);
        }

        var saveError = PreflightSaveable(document, job.Spec.Options);
        if (saveError is not null)
        {
            return JobStartResult.Fail(saveError);
        }

        var transition = Sync(_store.TransitionAsync(
            job.JobId,
            JobState.PreviewReady,
            JobState.Running,
            null,
            null,
            CancellationToken.None));

        if (transition is not null)
        {
            return JobStartResult.Fail(transition);
        }

        Sync(_store.SetCancelRequestedAsync(job.JobId, false, CancellationToken.None));
        Sync(_store.MarkStartedAsync(job.JobId, DateTime.UtcNow, CancellationToken.None));
        Sync(_store.AppendEventAsync(job.JobId, JobEventTypes.ApplyStarted, null, CancellationToken.None));

        _activeJobId = job.JobId;
        _wantsPump = true;

        RunSlice(document, TimeSpan.FromMilliseconds(job.Spec.Options.SliceBudgetMs));

        var updated = Sync(_store.GetAsync(job.JobId, CancellationToken.None)) ?? job;
        _wantsPump = JobStateMachine.NeedsPump(updated.State) && updated.State != JobState.Cancelling
            ? updated.State == JobState.Running
            : false;
        return JobStartResult.Ok(updated);
    }

    private JobStartResult StartResume(
        JobSpec spec,
        Document document,
        IJobAction action,
        string documentId)
    {
        var job = Sync(_store.GetAsync(spec.ResumeJobId!.Value, CancellationToken.None));
        if (job is null)
        {
            return JobStartResult.Fail(new JobError(
                JobErrorCodes.JobNotFound,
                "Job '" + spec.ResumeJobId + "' was not found",
                "call get_job_status to list recent jobs"));
        }

        if (!string.Equals(job.Spec.DocumentId, documentId, StringComparison.Ordinal))
        {
            return JobStartResult.Fail(JobErrors.DocumentMismatch(job.Spec.DocumentId, documentId));
        }

        if (!string.Equals(job.Spec.Action, spec.Action, StringComparison.OrdinalIgnoreCase))
        {
            return JobStartResult.Fail(JobErrors.Spec(
                "resume action '" + spec.Action + "' does not match job action '" + job.Spec.Action + "'"));
        }

        var saveError = PreflightSaveable(document, job.Spec.Options);
        if (saveError is not null)
        {
            return JobStartResult.Fail(saveError);
        }

        try
        {
            ReconcilePendingItems(job, document);
        }
        catch (Exception ex)
        {
            return JobStartResult.Fail(new JobError(
                JobErrorCodes.ItemFailed,
                "Crash reconciliation failed: " + ex.GetType().Name + ": " + ex.Message,
                "run get_job_report and rollback_job if needed; do not resume blindly"));
        }

        var from = job.State;
        if (from == JobState.Failed)
        {
            var restart = Sync(_store.TransitionAsync(
                job.JobId,
                JobState.Failed,
                JobState.Running,
                null,
                null,
                CancellationToken.None));
            if (restart is not null)
            {
                return JobStartResult.Fail(restart);
            }

            from = JobState.Running;
        }

        if (from == JobState.Interrupted)
        {
            // A job killed during preview resumes into the preview phase; anything
            // else resumes applying. The stored preview decides, not the caller.
            var target = string.IsNullOrWhiteSpace(job.PreviewJson)
                ? JobState.PreviewRunning
                : JobState.Running;

            var start = Sync(_store.TransitionAsync(
                job.JobId,
                JobState.Interrupted,
                target,
                null,
                null,
                CancellationToken.None));
            if (start is not null)
            {
                return JobStartResult.Fail(start);
            }
        }
        else if (from == JobState.CompletedWithFailures)
        {
            var retry = RetryFailed(document, job.JobId);
            if (retry.ErrorCode is not null)
            {
                return JobStartResult.Fail(new JobError(
                    retry.ErrorCode,
                    retry.ErrorDetail ?? "Retry failed",
                    "run get_job_status and get_job_report to inspect the job"));
            }

            var retried = Sync(_store.GetAsync(job.JobId, CancellationToken.None)) ?? job;
            _wantsPump = JobStateMachine.NeedsPump(retried.State);
            return JobStartResult.Ok(retried);
        }
        else if (from is not (JobState.Running or JobState.Failed))
        {
            return JobStartResult.Fail(JobErrors.InvalidState(
                "resume requires an interrupted or retryable job, but it is '" + from + "'",
                "check get_job_status; a completed job cannot be resumed"));
        }

        Sync(_store.SetCancelRequestedAsync(job.JobId, false, CancellationToken.None));
        Sync(_store.MarkStartedAsync(job.JobId, DateTime.UtcNow, CancellationToken.None));
        Sync(_store.AppendEventAsync(job.JobId, JobEventTypes.Resumed, null, CancellationToken.None));

        _activeJobId = job.JobId;
        _wantsPump = true;
        RunSlice(document, TimeSpan.FromMilliseconds(job.Spec.Options.SliceBudgetMs));

        var updated = Sync(_store.GetAsync(job.JobId, CancellationToken.None)) ?? job;
        _wantsPump = updated.State is JobState.Running or JobState.PreviewRunning or JobState.Cancelling;
        return JobStartResult.Ok(updated);
    }
}
