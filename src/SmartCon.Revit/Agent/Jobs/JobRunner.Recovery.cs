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
                updates.Add(new JobItemUpdate
                {
                    Seq = item.Seq,
                    State = JobItemState.Pending
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

        SmartConLogger.Info(
            "Reconciled job " + job.JobId.ToString("D") + ": kept=" + kept + ", redo=" + redone +
            ", durableCursor=" + job.LastSavedCursor);

        return new ReconcileOutcome { Kept = kept, Redone = redone };
    }

    /// <summary>Explicit rollback of an applied job (B.2).</summary>
    public JobRollbackResult Rollback(Document document, Guid jobId, bool dryRun)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", "Rollback"));

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
                or JobItemState.Applying or JobItemState.UndoFailed)
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
                    SmartConLogger.Warn(
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
            onlyUndoing: false);

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

        return new JobRollbackResult
        {
            JobId = jobId,
            Success = outcome.UndoFailed == 0,
            Restored = outcome.Restored,
            RestoreFailed = outcome.UndoFailed,
            Skipped = outcome.Skipped,
            State = target,
            ErrorCode = outcome.UndoFailed > 0 ? JobErrorCodes.RollbackFailed : null,
            ErrorDetail = outcome.UndoFailed > 0
                ? outcome.UndoFailed + " units could not be restored"
                : null
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

        var documentId = AgentDocumentIdentity.GetDocumentId(document);
        if (!string.Equals(job.Spec.DocumentId, documentId, StringComparison.Ordinal))
        {
            return new JobRetryResult
            {
                JobId = jobId,
                ErrorCode = JobErrorCodes.DocumentMismatch,
                ErrorDetail = JobErrors.DocumentMismatch(job.Spec.DocumentId, documentId).ToMessage(),
                State = job.State
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
                State = job.State
            };
        }

        var items = Sync(_store.LoadAllItemsAsync(jobId, CancellationToken.None));
        var failed = items.Where(item => item.State == JobItemState.Failed).Select(item => item.Seq).ToList();

        if (failed.Count == 0)
        {
            return new JobRetryResult
            {
                JobId = jobId,
                TotalFailed = 0,
                State = job.State,
                ErrorCode = JobErrorCodes.ItemFailed,
                ErrorDetail = "There are no failed units to retry [Action: run get_job_report to inspect the result]"
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
                State = job.State,
                ErrorCode = transition.Code,
                ErrorDetail = transition.ToMessage()
            };
        }

        Sync(_store.ResetItemsAsync(jobId, failed, JobItemState.Previewed, CancellationToken.None));

        Sync(_store.SetCancelRequestedAsync(jobId, false, CancellationToken.None));
        Sync(_store.AppendEventAsync(
            jobId,
            JobEventTypes.Resumed,
            "{\"retryFailed\":" + failed.Count + "}",
            CancellationToken.None));
        Sync(_store.MarkStartedAsync(jobId, DateTime.UtcNow, CancellationToken.None));

        _activeJobId = jobId;
        _wantsPump = true;
        RunSlice(document, TimeSpan.FromMilliseconds(job.Spec.Options.SliceBudgetMs));

        var updated = Sync(_store.GetAsync(jobId, CancellationToken.None));
        _wantsPump = updated is not null && JobStateMachine.NeedsPump(updated.State);

        return new JobRetryResult
        {
            JobId = jobId,
            Reset = failed.Count,
            TotalFailed = failed.Count,
            State = updated?.State ?? JobState.Running
        };
    }

    /// <summary>
    /// Undo loop shared by cancel-with-rollback and explicit rollback. Processes
    /// units in slices; each batch is one transaction for project actions.
    /// </summary>
    private UndoOutcome RunUndoBatches(
        JobRecord job,
        Document document,
        TimeSpan budget,
        long started,
        bool onlyUndoing)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", "RunUndoBatches"));

        var action = _actions[job.Spec.Action];
        var context = CreateContext(document, job.Spec.DocumentId, job.JobId, job.Spec, dryRun: false);
        var batchSize = Math.Max(1, job.Spec.Options.BatchSize);

        if (!onlyUndoing)
        {
            var applied = Sync(_store.LoadItemsAsync(
                job.JobId,
                new[]
                {
                    JobItemState.Done, JobItemState.Mismatch,
                    JobItemState.Applying, JobItemState.UndoFailed
                },
                0,
                int.MaxValue,
                CancellationToken.None));

            if (applied.Count > 0)
            {
                Sync(_store.ResetItemsAsync(
                    job.JobId,
                    applied.Select(item => item.Seq).ToList(),
                    JobItemState.Undoing,
                    CancellationToken.None));
            }
        }

        var restored = 0;
        var undoFailed = 0;
        var skipped = 0;

        while (true)
        {
            var items = Sync(_store.LoadItemsAsync(
                job.JobId,
                new[] { JobItemState.Undoing },
                0,
                batchSize,
                CancellationToken.None));

            if (items.Count == 0)
            {
                break;
            }

            if (IsOverBudget(started, budget) && restored + undoFailed > 0)
            {
                break;
            }

            var updates = new List<JobItemUpdate>(items.Count);
            ITransactionScope? scope = null;

            try
            {
                if (action.Scope is JobActionScope.ProjectInstance or JobActionScope.ProjectType)
                {
                    scope = _transactions.BeginTransaction(document, "SmartCon job rollback: " + action.Name);
                }

                foreach (var item in items)
                {
                    JobUnitUndo undo;
                    try
                    {
                        undo = action.Undo(context, ToUnit(item), item.UndoJson, dryRun: false) ??
                               new JobUnitUndo
                               {
                                   ErrorCode = JobErrorCodes.RollbackFailed,
                                   ErrorDetail = "Action returned no undo result"
                               };
                    }
                    catch (Exception ex)
                    {
                        undo = new JobUnitUndo
                        {
                            ErrorCode = JobErrorCodes.RollbackFailed,
                            ErrorDetail = ex.GetType().Name + ": " + ex.Message
                        };
                    }

                    if (undo.Success && undo.Skipped)
                    {
                        skipped++;
                        updates.Add(new JobItemUpdate
                        {
                            Seq = item.Seq,
                            State = JobItemState.Undone,
                            ActualJson = undo.ActualJson
                        });
                    }
                    else if (undo.Success)
                    {
                        restored++;
                        updates.Add(new JobItemUpdate
                        {
                            Seq = item.Seq,
                            State = JobItemState.Undone,
                            ActualJson = undo.ActualJson
                        });
                    }
                    else
                    {
                        undoFailed++;
                        updates.Add(new JobItemUpdate
                        {
                            Seq = item.Seq,
                            State = JobItemState.UndoFailed,
                            ErrorCode = undo.ErrorCode ?? JobErrorCodes.RollbackFailed,
                            ErrorDetail = undo.ErrorDetail
                        });
                    }
                }

                if (scope is not null)
                {
                    var status = scope.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        undoFailed += items.Count;
                        updates.Clear();
                        foreach (var item in items)
                        {
                            updates.Add(new JobItemUpdate
                            {
                                Seq = item.Seq,
                                State = JobItemState.UndoFailed,
                                ErrorCode = JobErrorCodes.RollbackFailed,
                                ErrorDetail = "Revit rolled back the undo batch (status " + status + ")"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                scope?.RollBack();
                undoFailed += items.Count;
                updates.Clear();
                foreach (var item in items)
                {
                    updates.Add(new JobItemUpdate
                    {
                        Seq = item.Seq,
                        State = JobItemState.UndoFailed,
                        ErrorCode = JobErrorCodes.RollbackFailed,
                        ErrorDetail = ex.GetType().Name + ": " + ex.Message
                    });
                }

                SmartConLogger.Error(
                    "Rollback batch failed: " + ex.GetType().Name + ": " + ex.Message +
                    " [Action: run rollback_job again or restore the model from the backup file]");
            }
            finally
            {
                scope?.Dispose();
            }

            if (updates.Count > 0)
            {
                Sync(_store.UpdateItemsAsync(job.JobId, updates, CancellationToken.None));
            }
        }

        var remaining = Sync(_store.LoadItemsAsync(
            job.JobId,
            new[] { JobItemState.Undoing },
            0,
            1,
            CancellationToken.None)).Count;

        JobError? saveError = null;
        if (restored > 0)
        {
            saveError = SaveNow(job, document, job.LastSavedCursor);
        }

        return new UndoOutcome
        {
            Restored = restored,
            UndoFailed = undoFailed,
            Skipped = skipped,
            Processed = restored + undoFailed + skipped,
            HasMoreWork = remaining > 0,
            SaveError = saveError
        };
    }

    private sealed class ReconcileOutcome
    {
        public int Kept { get; init; }

        public int Redone { get; init; }
    }

    private sealed class UndoOutcome
    {
        public int Restored { get; init; }

        public int UndoFailed { get; init; }

        public int Skipped { get; init; }

        public int Processed { get; init; }

        public bool HasMoreWork { get; init; }

        public JobError? SaveError { get; init; }
    }
}