using System.Diagnostics;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>Crash reconciliation, rollback and retry.</summary>
public sealed partial class JobRunner
{
    /// <summary>
    /// Re-verifies units whose durability is unknown after a crash: every unit in
    /// <see cref="JobItemState.Applying"/> and every done unit beyond the durable
    /// cursor. Verified units stay done (idempotent redo), the rest are queued for
    /// re-application. This is what makes "kill before Save" safe.
    /// </summary>
    private ReconcileOutcome ReconcilePendingItems(JobRecord job, Document document)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", "ReconcilePendingItems"));

        var action = _actions[job.Spec.Action];
        var context = CreateContext(document, job.Spec.DocumentId, job.JobId, job.Spec, dryRun: false);
        var items = Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None));

        var candidates = items
            .Where(item => item.State == JobItemState.Applying ||
                           (item.State is JobItemState.Done or JobItemState.Mismatch &&
                            item.Seq > job.LastSavedCursor))
            .ToList();

        if (candidates.Count == 0)
        {
            return new ReconcileOutcome();
        }

        var updates = new List<JobItemUpdate>(candidates.Count);
        var kept = 0;
        var redone = 0;

        foreach (var item in candidates)
        {
            var preview = JobJson.Deserialize<JobUnitPreview>(item.PreviewJson);
            if (preview is null)
            {
                // No dry-run data: the unit cannot be re-applied safely and must be
                // visible in the report instead of lingering as Pending forever.
                updates.Add(new JobItemUpdate
                {
                    Seq = item.Seq,
                    State = JobItemState.Failed,
                    ErrorCode = JobErrorCodes.PreviewRequired,
                    ErrorDetail = "The unit has no stored preview data " +
                                  "[Action: run a fresh preview for this unit]"
                });
                redone++;
                continue;
            }

            JobUnitVerification verification;
            try
            {
                verification = action.Verify(context, ToUnit(item), preview);
            }
            catch (Exception ex)
            {
                verification = new JobUnitVerification
                {
                    Verified = false,
                    MismatchReason = ex.GetType().Name + ": " + ex.Message
                };
            }

            if (verification.Verified)
            {
                updates.Add(new JobItemUpdate
                {
                    Seq = item.Seq,
                    State = preview.Skipped ? JobItemState.Skipped : JobItemState.Done,
                    ActualJson = verification.ActualJson
                });
                kept++;
            }
            else
            {
                updates.Add(new JobItemUpdate
                {
                    Seq = item.Seq,
                    State = JobItemState.Previewed,
                    ErrorCode = null,
                    ErrorDetail = null
                });
                redone++;
            }
        }

        Sync(_store.UpdateItemsAsync(job.JobId, updates, CancellationToken.None));
        Sync(_store.AppendEventAsync(
            job.JobId,
            JobEventTypes.Reconciled,
            "{\"kept\":" + kept + ",\"redone\":" + redone + "}",
            CancellationToken.None));

        JobLog.Info("ReconcilePendingItems",
            "Reconciled job " + job.JobId.ToString("D") + ": kept=" + kept + ", redo=" + redone +
            ", durableCursor=" + job.LastSavedCursor);

        return new ReconcileOutcome { Kept = kept, Redone = redone };
    }

    /// <summary>Explicit rollback of an applied job (B.2).</summary>
    public JobRollbackResult Rollback(Document document, Guid jobId, bool dryRun)
    {
        var job = Sync(_store.GetAsync(jobId, CancellationToken.None));
        if (job is null)
        {
            return new JobRollbackResult
            {
                JobId = jobId,
                ErrorCode = JobErrorCodes.JobNotFound,
                ErrorDetail = JobErrors.RollbackUnavailable("the job was not found").ToMessage()
            };
        }

        var documentId = AgentDocumentIdentity.GetDocumentId(document);
        if (!string.Equals(job.Spec.DocumentId, documentId, StringComparison.Ordinal))
        {
            return new JobRollbackResult
            {
                JobId = jobId,
                ErrorCode = JobErrorCodes.DocumentMismatch,
                ErrorDetail = JobErrors.DocumentMismatch(job.Spec.DocumentId, documentId).ToMessage()
            };
        }

        if (job.State is JobState.Running or JobState.PreviewRunning or JobState.Cancelling)
        {
            return new JobRollbackResult
            {
                JobId = jobId,
                ErrorCode = JobErrorCodes.InvalidState,
                ErrorDetail = JobErrors.InvalidState(
                    "the job is still " + job.State,
                    "cancel the job first (cancel_job), then call rollback_job").ToMessage(),
                State = job.State
            };
        }

        if (!_actions.TryGetValue(job.Spec.Action, out var action))
        {
            return new JobRollbackResult
            {
                JobId = jobId,
                ErrorCode = JobErrorCodes.ActionUnknown,
                ErrorDetail = JobErrors.ActionUnknown(job.Spec.Action).ToMessage(),
                State = job.State
            };
        }

        var items = Sync(_store.LoadAllItemsAsync(jobId, CancellationToken.None));
        var undoable = items
            .Where(item => item.State is JobItemState.Done or JobItemState.Mismatch
                or JobItemState.Applying or JobItemState.Undoing or JobItemState.UndoFailed)
            .ToList();

        if (undoable.Count == 0)
        {
            return new JobRollbackResult
            {
                JobId = jobId,
                ErrorCode = JobErrorCodes.RollbackUnavailable,
                ErrorDetail = JobErrors.RollbackUnavailable("no applied units with undo data").ToMessage(),
                State = job.State
            };
        }

        if (dryRun)
        {
            var context = CreateContext(document, documentId, jobId, job.Spec, dryRun: true);
            var restorable = 0;
            var failed = 0;
            var skipped = 0;

            foreach (var item in undoable)
            {
                try
                {
                    var result = action.Undo(context, ToUnit(item), item.UndoJson, dryRun: true);
                    if (result.Success && result.Skipped)
                    {
                        skipped++;
                    }
                    else if (result.Success)
                    {
                        restorable++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    JobLog.Warn("Rollback",
                        "Undo dry run failed for unit " + item.UnitKey + ": " +
                        ex.GetType().Name + ": " + ex.Message +
                        " [Action: fix the cause, then run rollback_job without dryRun]");
                    failed++;
                }
            }

            return new JobRollbackResult
            {
                JobId = jobId,
                Success = failed == 0,
                DryRun = true,
                Restored = restorable,
                RestoreFailed = failed,
                Skipped = skipped,
                State = job.State
            };
        }

        Sync(_store.AppendEventAsync(jobId, JobEventTypes.RollbackStarted, null, CancellationToken.None));

        var outcome = RunUndoBatches(
            job,
            document,
            TimeSpan.FromSeconds(45),
            Stopwatch.GetTimestamp(),
            singleBatchPerSlice: false);

        if (outcome.HasMoreWork)
        {
            var partial = Sync(_store.TransitionAsync(
                jobId,
                job.State,
                JobState.RollbackPartial,
                JobErrorCodes.RollbackFailed,
                "Rollback is still in progress; " + outcome.Restored + " unit(s) restored so far",
                CancellationToken.None));

            if (partial is null)
            {
                Sync(_store.UpdateTotalsAsync(
                    jobId,
                    JobJson.Serialize(JobTotals.FromItems(
                        Sync(_store.LoadAllItemsAsync(jobId, CancellationToken.None)))),
                    CancellationToken.None));
            }

            return new JobRollbackResult
            {
                JobId = jobId,
                Success = false,
                Restored = outcome.Restored,
                RestoreFailed = outcome.UndoFailed,
                Skipped = outcome.Skipped,
                State = partial is null ? JobState.RollbackPartial : job.State,
                ErrorCode = JobErrorCodes.RollbackFailed,
                ErrorDetail = "Rollback did not finish within the time slice; call rollback_job again to continue"
            };
        }

        var target = outcome.UndoFailed > 0 || outcome.SaveError is not null
            ? JobState.RollbackPartial
            : JobState.RolledBack;

        var transition = Sync(_store.TransitionAsync(
            jobId,
            job.State,
            target,
            outcome.UndoFailed > 0 ? JobErrorCodes.RollbackFailed : null,
            outcome.UndoFailed > 0
                ? JobErrors.RollbackFailed(outcome.UndoFailed + " units could not be restored").ToMessage()
                : null,
            CancellationToken.None));

        if (transition is not null)
        {
            return new JobRollbackResult
            {
                JobId = jobId,
                ErrorCode = transition.Code,
                ErrorDetail = transition.ToMessage(),
                State = job.State,
                Restored = outcome.Restored,
                RestoreFailed = outcome.UndoFailed
            };
        }

        Sync(_store.MarkFinishedAsync(
            jobId,
            target,
            outcome.UndoFailed > 0 ? JobErrorCodes.RollbackFailed : null,
            outcome.UndoFailed > 0 ? outcome.UndoFailed + " units could not be restored" : null,
            CancellationToken.None));
        Sync(_store.AppendEventAsync(
            jobId,
            JobEventTypes.RollbackCompleted,
            "{\"restored\":" + outcome.Restored + ",\"failed\":" + outcome.UndoFailed + "}",
            CancellationToken.None));
        Sync(_store.UpdateTotalsAsync(
            jobId,
            JobJson.Serialize(JobTotals.FromItems(
                Sync(_store.LoadAllItemsAsync(jobId, CancellationToken.None)))),
            CancellationToken.None));

        var failureDetail = outcome.UndoFailed > 0
            ? outcome.UndoFailed + " units could not be restored"
            : outcome.SaveError?.ToMessage();

        return new JobRollbackResult
        {
            JobId = jobId,
            Success = outcome.UndoFailed == 0 && outcome.SaveError is null,
            Restored = outcome.Restored,
            RestoreFailed = outcome.UndoFailed,
            Skipped = outcome.Skipped,
            State = target,
            ErrorCode = outcome.UndoFailed > 0 || outcome.SaveError is not null
                ? JobErrorCodes.RollbackFailed
                : null,
            ErrorDetail = failureDetail
        };
    }

    /// <summary>Resets failed units and resumes the job (B.2). Failed units only.</summary>
    public JobRetryResult RetryFailed(Document document, Guid jobId)
    {
        var job = Sync(_store.GetAsync(jobId, CancellationToken.None));
        if (job is null)
        {
            return new JobRetryResult
            {
                JobId = jobId,
                ErrorCode = JobErrorCodes.JobNotFound,
                ErrorDetail = "The job was not found"
            };
        }

        var items = Sync(_store.LoadAllItemsAsync(jobId, CancellationToken.None));
        var documentId = AgentDocumentIdentity.GetDocumentId(document);
        if (!string.Equals(job.Spec.DocumentId, documentId, StringComparison.Ordinal))
        {
            return new JobRetryResult
            {
                JobId = jobId,
                ErrorCode = JobErrorCodes.DocumentMismatch,
                ErrorDetail = JobErrors.DocumentMismatch(job.Spec.DocumentId, documentId).ToMessage(),
                State = job.State,
                Totals = JobTotals.FromItems(items)
            };
        }

        if (job.State is not (JobState.CompletedWithFailures or JobState.Failed))
        {
            return new JobRetryResult
            {
                JobId = jobId,
                ErrorCode = JobErrorCodes.InvalidState,
                ErrorDetail = JobErrors.InvalidState(
                    "retry requires a job in CompletedWithFailures or Failed, but it is '" + job.State + "'",
                    "call get_job_status; only jobs with failed units can be retried").ToMessage(),
                State = job.State,
                Totals = JobTotals.FromItems(items)
            };
        }

        if (!_actions.TryGetValue(job.Spec.Action, out var action))
        {
            return new JobRetryResult
            {
                JobId = jobId,
                ErrorCode = JobErrorCodes.ActionUnknown,
                ErrorDetail = JobErrors.ActionUnknown(job.Spec.Action).ToMessage(),
                State = job.State,
                Totals = JobTotals.FromItems(items)
            };
        }

        var failed = items.Where(item => item.State == JobItemState.Failed).ToList();

        if (failed.Count == 0)
        {
            return new JobRetryResult
            {
                JobId = jobId,
                TotalFailed = 0,
                State = job.State,
                Totals = JobTotals.FromItems(items),
                ErrorCode = JobErrorCodes.ItemFailed,
                ErrorDetail = "There are no failed units to retry [Action: run get_job_report to inspect the result]"
            };
        }

        var saveError = PreflightSaveable(document, job.Spec.Options);
        if (saveError is not null)
        {
            return new JobRetryResult
            {
                JobId = jobId,
                TotalFailed = failed.Count,
                Attempted = 0,
                State = job.State,
                Totals = JobTotals.FromItems(items),
                ErrorCode = saveError.Code,
                ErrorDetail = saveError.ToMessage()
            };
        }

        var largeGuard = ValidateLargeJob(failed.Count, job.Spec.Options);
        if (largeGuard is not null)
        {
            return new JobRetryResult
            {
                JobId = jobId,
                TotalFailed = failed.Count,
                Attempted = 0,
                State = job.State,
                Totals = JobTotals.FromItems(items),
                ErrorCode = largeGuard.Code,
                ErrorDetail = largeGuard.ToMessage()
            };
        }

        // A failed unit may have failed in the dry run (readonly parameter, missing
        // element) or in apply (preview stale). Retrying means re-evaluating the
        // frozen unit against the current model, never reusing a failed preview.
        var context = CreateContext(document, job.Spec.DocumentId, jobId, job.Spec, dryRun: true);
        var updates = new List<JobItemUpdate>(failed.Count);
        var reset = 0;
        var stillFailed = 0;

        foreach (var item in failed)
        {
            JobUnitPreview preview;
            try
            {
                preview = action.Preview(context, ToUnit(item)) ?? new JobUnitPreview
                {
                    ErrorCode = item.ErrorCode ?? JobErrorCodes.ItemFailed,
                    ErrorDetail = item.ErrorDetail ?? "The action returned no preview"
                };
            }
            catch (Exception ex)
            {
                preview = new JobUnitPreview
                {
                    ErrorCode = item.ErrorCode ?? JobErrorCodes.ItemFailed,
                    ErrorDetail = (item.ErrorDetail ?? "Preview failed") +
                                  " (" + ex.GetType().Name + ": " + ex.Message + ")"
                };
            }

            var state = !preview.Success
                ? JobItemState.Failed
                : preview.Skipped
                    ? JobItemState.Skipped
                    : JobItemState.Previewed;
            if (state == JobItemState.Previewed)
            {
                reset++;
            }
            else if (state == JobItemState.Failed)
            {
                stillFailed++;
            }

            updates.Add(new JobItemUpdate
            {
                Seq = item.Seq,
                State = state,
                PreviewJson = JobJson.Serialize(preview),
                ExpectedJson = preview.ExpectedJson,
                ActualJson = preview.ActualJson,
                UndoJson = preview.UndoJson,
                ErrorCode = preview.ErrorCode,
                ErrorDetail = preview.ErrorDetail,
                IncrementAttempts = true
            });
        }

        Sync(_store.UpdateItemsAsync(jobId, updates, CancellationToken.None));
        var refreshed = Sync(_store.LoadAllItemsAsync(jobId, CancellationToken.None));
        var totals = JobTotals.FromItems(refreshed);
        Sync(_store.UpdateTotalsAsync(jobId, JobJson.Serialize(totals), CancellationToken.None));
        JobLog.Info("RetryFailed",
            "Job " + jobId.ToString("D") + ": attempted=" + failed.Count +
            ", reset=" + reset + ", stillFailed=" + stillFailed);

        var firstFailure = refreshed.FirstOrDefault(item => item.State == JobItemState.Failed);
        if (reset == 0 && stillFailed == 0)
        {
            // Every failed unit already holds its target value: nothing to apply.
            return new JobRetryResult
            {
                JobId = jobId,
                Reset = 0,
                TotalFailed = failed.Count,
                StillFailed = 0,
                Attempted = failed.Count,
                State = job.State,
                Totals = totals
            };
        }

        if (reset == 0)
        {
            // Nothing can be applied: stay in the terminal state, but return the
            // real cause instead of a generic "no usable preview data".
            return new JobRetryResult
            {
                JobId = jobId,
                Reset = 0,
                TotalFailed = failed.Count,
                StillFailed = stillFailed,
                Attempted = failed.Count,
                State = job.State,
                Totals = totals,
                ErrorCode = firstFailure?.ErrorCode ?? JobErrorCodes.ItemFailed,
                ErrorDetail = firstFailure?.ErrorDetail ??
                              "All failed units failed the fresh preview again"
            };
        }

        var transition = Sync(_store.TransitionAsync(
            jobId,
            job.State,
            JobState.Running,
            null,
            null,
            CancellationToken.None));

        if (transition is not null)
        {
            return new JobRetryResult
            {
                JobId = jobId,
                Reset = 0,
                TotalFailed = failed.Count,
                StillFailed = stillFailed,
                Attempted = failed.Count,
                State = job.State,
                Totals = totals,
                ErrorCode = transition.Code,
                ErrorDetail = transition.ToMessage()
            };
        }

        Sync(_store.SetCancelRequestedAsync(jobId, false, CancellationToken.None));
        Sync(_store.AppendEventAsync(
            jobId,
            JobEventTypes.Resumed,
            "{\"retryFailed\":" + failed.Count + ",\"requeued\":" + reset + "}",
            CancellationToken.None));
        Sync(_store.MarkStartedAsync(jobId, DateTime.UtcNow, CancellationToken.None));

        _activeJobId = jobId;
        _wantsPump = true;
        RunSlice(document, TimeSpan.FromMilliseconds(job.Spec.Options.SliceBudgetMs));

        var updated = Sync(_store.GetAsync(jobId, CancellationToken.None));
        _wantsPump = updated is not null && JobStateMachine.NeedsPump(updated.State);
        var finalItems = Sync(_store.LoadAllItemsAsync(jobId, CancellationToken.None));

        return new JobRetryResult
        {
            JobId = jobId,
            Reset = reset,
            TotalFailed = failed.Count,
            StillFailed = stillFailed,
            Attempted = failed.Count,
            State = updated?.State ?? JobState.Running,
            Totals = JobTotals.FromItems(finalItems)
        };
    }

    private sealed class ReconcileOutcome
    {
        public int Kept { get; init; }

        public int Redone { get; init; }
    }
}