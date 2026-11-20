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
        var supersedable = active is not null &&
                           active.State is JobState.PreviewReady or JobState.AwaitingApproval;
        if (active is not null && !supersedable)
        {
            return JobStartResult.Fail(JobErrors.AlreadyActive(active.JobId.ToString("D")));
        }

        // A preview only rolls back a dry run and never saves, so an unsaved
        // document is fine here. Apply/resume still require a saveable file.

        // Expand before superseding the old preview: a typo in the new spec must
        // not destroy a valid preview that the agent can still apply.
        var validationContext = CreateContext(
            document, documentId, Guid.NewGuid(), spec, dryRun: true);

        JobExpansion expansion;
        try
        {
            expansion = action.Expand(validationContext, spec.SpecJson);
        }
        catch (Exception ex)
        {
            return JobStartResult.Fail(new JobError(
                JobErrorCodes.InvalidSpec,
                "Action '" + action.Name + "' failed to expand the selector: " + ex.Message,
                "fix the spec arguments and call start_job again"));
        }

        if (!expansion.Success)
        {
            return JobStartResult.Fail(new JobError(
                expansion.ErrorCode!,
                expansion.ErrorDetail ?? "Expansion failed",
                "fix the spec arguments and call start_job again"));
        }

        if (expansion.Units.Count == 0)
        {
            return JobStartResult.Fail(JobErrors.SelectorEmpty(action.Name));
        }

        var limit = Math.Min(action.MaxUnits, spec.Options.MaxItems);
        if (expansion.Units.Count > limit)
        {
            return JobStartResult.Fail(JobErrors.LimitExceeded(expansion.Units.Count, limit));
        }

        // A completed preview locks nothing: supersede it instead of forcing the
        // agent to cancel first (the old digest becomes unusable anyway).
        if (active is not null)
        {
            var superseded = SupersedePreview(active);
            if (superseded is not null)
            {
                return JobStartResult.Fail(superseded);
            }
        }

        var record = Sync(_store.CreateAsync(spec, CancellationToken.None));

        lock (_gate)
        {
            if (_previewWarnings.Count >= 16)
            {
                _previewWarnings.Clear();
            }

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
                "run start_job with dryRun=true first and apply with the returned previewDigest"));
        }

        if (!string.Equals(job.Spec.Action, spec.Action, StringComparison.OrdinalIgnoreCase))
        {
            return JobStartResult.Fail(JobErrors.PreviewMismatch());
        }

        if (job.State != JobState.PreviewReady)
        {
            return JobStartResult.Fail(JobErrors.InvalidState(
                "apply requires a job in state PreviewReady, but it is '" + job.State + "'",
                "run start_job with dryRun=true again and apply the fresh previewDigest"));
        }

        var preview = ParsePreview(job.PreviewJson);
        if (preview is null)
        {
            return JobStartResult.Fail(JobErrors.Preview(
                "the stored preview is unreadable",
                "run start_job with dryRun=true again"));
        }

        var envelope = JobSpecValidator.ValidateApplyEnvelope(preview, spec, DateTime.UtcNow);
        if (envelope is not null)
        {
            return JobStartResult.Fail(envelope);
        }

        // Apply-time policies (onError, rollbackOnError, savePolicy, ...) belong to
        // this run, but they must not reset the preview's other options to defaults.
        var effectiveSpec = WithEffectiveOptions(
            job.Spec, spec.OptionsOverride, JobMode.Apply, spec.Options.AllowLargeJob);
        var largeGuard = ValidateLargeJob(preview.Count, effectiveSpec.Options);
        if (largeGuard is not null)
        {
            JobLog.Warn("StartApply",
                "Job " + job.JobId.ToString("D") + " refused by the large-job guard: " +
                preview.Count + " units [Action: review the preview, then retry with allowLargeJobs=true]");
            return JobStartResult.Fail(largeGuard);
        }

        // onError=abort is a policy of the whole run: the dry run already knows
        // which units cannot be applied, so applying past them would contradict it.
        if (effectiveSpec.Options.OnError == JobOnError.Abort && preview.FailedCount > 0)
        {
            JobLog.Warn("StartApply",
                "Job " + job.JobId.ToString("D") + " refused by onError=abort: " +
                preview.FailedCount + " preview failure(s) [Action: fix the failing units and preview again]");
            return JobStartResult.Fail(JobErrors.AbortedByPolicy(
                preview.FailedCount + " unit(s) already failed the preview; nothing was applied"));
        }

        var saveError = PreflightSaveable(document, effectiveSpec.Options);
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

        // Persist apply-time policies only after the state transition succeeded:
        // a rejected transition must not leave a Running-mode spec on a preview.
        Sync(_store.UpdateSpecAsync(job.JobId, effectiveSpec, CancellationToken.None));
        job = Sync(_store.GetAsync(job.JobId, CancellationToken.None)) ?? job;

        Sync(_store.SetCancelRequestedAsync(job.JobId, false, CancellationToken.None));
        Sync(_store.MarkStartedAsync(job.JobId, DateTime.UtcNow, CancellationToken.None));
        Sync(_store.AppendEventAsync(job.JobId, JobEventTypes.ApplyStarted, null, CancellationToken.None));

        _activeJobId = job.JobId;
        _wantsPump = true;

        RunSlice(document, TimeSpan.FromMilliseconds(job.Spec.Options.SliceBudgetMs));

        var updated = Sync(_store.GetAsync(job.JobId, CancellationToken.None)) ?? job;
        _wantsPump = JobStateMachine.NeedsPump(updated.State);
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

        var effectiveSpec = WithEffectiveOptions(
            job.Spec, spec.OptionsOverride, JobMode.Resume, spec.Options.AllowLargeJob);
        var itemCount = Sync(_store.CountItemsAsync(job.JobId, CancellationToken.None));
        var largeGuard = ValidateLargeJob(itemCount, effectiveSpec.Options);
        if (largeGuard is not null)
        {
            JobLog.Warn("StartResume",
                "Job " + job.JobId.ToString("D") + " refused by the large-job guard: " +
                itemCount + " units [Action: retry with allowLargeJobs=true]");
            return JobStartResult.Fail(largeGuard);
        }

        var saveError = PreflightSaveable(document, effectiveSpec.Options);
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
        if (from is not (JobState.Interrupted or JobState.Running or JobState.Failed
            or JobState.CompletedWithFailures))
        {
            return JobStartResult.Fail(JobErrors.InvalidState(
                "resume requires an interrupted or retryable job, but it is '" + from + "'",
                "check get_job_status; a completed job cannot be resumed"));
        }

        if (from is JobState.Failed or JobState.CompletedWithFailures)
        {
            Sync(_store.UpdateSpecAsync(job.JobId, effectiveSpec, CancellationToken.None));
            job = Sync(_store.GetAsync(job.JobId, CancellationToken.None)) ?? job;
            return ResumeByRetry(document, job);
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

        // Persist resume-time policies after the state transition succeeded.
        Sync(_store.UpdateSpecAsync(job.JobId, effectiveSpec, CancellationToken.None));
        job = Sync(_store.GetAsync(job.JobId, CancellationToken.None)) ?? job;

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

    private JobStartResult ResumeByRetry(Document document, JobRecord job)
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

    /// <summary>
    /// A ready preview is stateless: it changed nothing, so a new preview replaces
    /// it instead of locking the document for the whole TTL. The old digest stops
    /// working because the job leaves the PreviewReady state.
    /// </summary>
    private JobError? SupersedePreview(JobRecord active)
    {
        var transition = Sync(_store.TransitionAsync(
            active.JobId,
            active.State,
            JobState.Cancelled,
            JobErrorCodes.PreviewSuperseded,
            "Superseded by a new preview; the previous digest is no longer valid",
            CancellationToken.None));

        if (transition is not null)
        {
            return transition;
        }

        Sync(_store.MarkFinishedAsync(
            active.JobId,
            JobState.Cancelled,
            JobErrorCodes.PreviewSuperseded,
            "Superseded by a new preview",
            CancellationToken.None));
        Sync(_store.AppendEventAsync(
            active.JobId,
            JobEventTypes.PreviewSuperseded,
            null,
            CancellationToken.None));
        lock (_gate)
        {
            _previewWarnings.Remove(active.JobId);
        }

        JobLog.Info("SupersedePreview",
            "Job " + active.JobId.ToString("D") + " preview superseded by a new preview");
        return null;
    }

    private static JobSpec WithEffectiveOptions(
        JobSpec stored,
        JobOptionsOverride? overrides,
        JobMode mode,
        bool allowLargeAck = false)
    {
        if (mode != JobMode.Preview && allowLargeAck && overrides?.AllowLargeJob != true)
        {
            // allowLargeJobs=true is also accepted from the apply/resume call's
            // top-level options (the tool sets both); a preview can never store it.
            overrides = new JobOptionsOverride
            {
                OnError = overrides?.OnError,
                RollbackOnError = overrides?.RollbackOnError,
                SavePolicy = overrides?.SavePolicy,
                BatchSize = overrides?.BatchSize,
                MaxItems = overrides?.MaxItems,
                AllowLargeJob = true
            };
        }

        var options = JobOptionsMerger.Merge(stored.Options, overrides);
        if (mode == JobMode.Preview)
        {
            // allowLargeJobs is an apply/resume acknowledgement only; letting a
            // preview store it would satisfy the apply-time gate without review.
            options = JobOptionsMerger.Merge(
                options,
                new JobOptionsOverride { AllowLargeJob = false });
        }

        return new JobSpec
        {
            SchemaVersion = stored.SchemaVersion,
            DocumentId = stored.DocumentId,
            Action = stored.Action,
            Mode = mode,
            Goal = stored.Goal,
            SpecJson = stored.SpecJson,
            Options = options,
            OptionsOverride = null,
            PreviewDigest = stored.PreviewDigest,
            ResumeJobId = stored.ResumeJobId,
            ConversationId = stored.ConversationId
        };
    }

    private static JobError? ValidateLargeJob(int count, JobOptions options)
    {
        if (count <= JobOptions.LargeJobThreshold || options.AllowLargeJob)
        {
            return null;
        }

        return JobErrors.LargeJobConfirmation(count, JobOptions.LargeJobThreshold);
    }
}
