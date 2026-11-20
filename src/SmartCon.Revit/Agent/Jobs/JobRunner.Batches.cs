using System.Diagnostics;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>Batch apply, verification and finalization of the job runner.</summary>
public sealed partial class JobRunner
{
    private ApplyBatchOutcome ApplyBatch(
        JobRecord job,
        Document document,
        IJobAction action,
        JobActionContext context,
        IReadOnlyList<JobItem> items)
    {
        var fromSeq = items[0].Seq;
        var toSeq = items[items.Count - 1].Seq;
        var batchIndex = Sync(_store.BeginBatchAsync(job.JobId, fromSeq, toSeq, CancellationToken.None));

        var plans = new List<BatchPlan>(items.Count);
        foreach (var item in items)
        {
            var preview = JobJson.Deserialize<JobUnitPreview>(item.PreviewJson);
            if (preview is null || !preview.Success)
            {
                plans.Add(new BatchPlan
                {
                    Item = item,
                    Preview = preview ?? PreviewFailure("job_preview_stale", "Preview data is missing"),
                    Error = new JobError(
                        JobErrorCodes.PreviewRequired,
                        "Unit '" + item.UnitKey + "' has no usable preview data",
                        "run start_job with mode=preview again")
                });
                continue;
            }

            if (!preview.Skipped && string.IsNullOrWhiteSpace(preview.UndoJson))
            {
                plans.Add(new BatchPlan
                {
                    Item = item,
                    Preview = preview,
                    Error = new JobError(
                        "job_undo_unavailable",
                        "Unit '" + item.UnitKey + "' has no undo payload, so it cannot be written safely",
                        "run the preview again; a unit without undo data is never applied (J-04)")
                });
                continue;
            }

            plans.Add(new BatchPlan { Item = item, Preview = preview });
        }

        // J-04: intent + undo go to SQLite before the model changes. Undo payloads
        // were computed at preview time from the same read value that the apply
        // step re-checks (preview-stale guard inside the action).
        var intents = plans
            .Where(plan => plan.Error is null && !plan.Preview.Skipped && !string.IsNullOrWhiteSpace(plan.Preview.UndoJson))
            .Select(plan => new JobItemUpdate
            {
                Seq = plan.Item.Seq,
                State = JobItemState.Applying,
                ExpectedJson = plan.Preview.ExpectedJson,
                ActualJson = plan.Preview.ActualJson,
                UndoJson = plan.Preview.UndoJson,
                IncrementAttempts = true,
                BatchIndex = batchIndex
            })
            .ToList();

        if (intents.Count > 0)
        {
            Sync(_store.UpdateItemsAsync(job.JobId, intents, CancellationToken.None));
        }

        var applied = new Dictionary<int, JobUnitApply>();
        var abort = false;
        ITransactionScope? scope = null;

        try
        {
            if (action.Scope is JobActionScope.ProjectInstance or JobActionScope.ProjectType)
            {
                scope = _transactions.BeginTransaction(document, "SmartCon job: " + action.Name);
            }

            foreach (var plan in plans)
            {
                if (plan.Error is not null)
                {
                    continue;
                }

                if (plan.Preview.Skipped)
                {
                    applied[plan.Item.Seq] = new JobUnitApply
                    {
                        Success = true,
                        Skipped = true,
                        ExpectedJson = plan.Preview.ExpectedJson,
                        UndoJson = plan.Preview.UndoJson
                    };
                    continue;
                }

                try
                {
                    var unit = ToUnit(plan.Item);
                    Action<string>? persistUndo =
                        action.Scope is JobActionScope.FamilyDefinition or JobActionScope.RfaFile
                            ? payload => PersistUndoIntent(job, plan.Item.Seq, payload)
                            : null;

                    var result = action.Apply(
                                     context,
                                     unit,
                                     plan.Preview,
                                     plan.Item.Attempts + 1,
                                     persistUndo)
                                 ?? new JobUnitApply
                                 {
                                     ErrorCode = JobErrorCodes.ItemFailed,
                                     ErrorDetail = "Action returned no result"
                                 };

                    applied[plan.Item.Seq] = result;

                    if (!result.Success && job.Spec.Options.OnError == JobOnError.Abort)
                    {
                        abort = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    applied[plan.Item.Seq] = new JobUnitApply
                    {
                        ErrorCode = JobErrorCodes.ItemFailed,
                        ErrorDetail = ex.GetType().Name + ": " + ex.Message
                    };

                    if (job.Spec.Options.OnError == JobOnError.Abort)
                    {
                        abort = true;
                        break;
                    }
                }
            }

            if (scope is not null)
            {
                if (abort)
                {
                    scope.RollBack();
                }
                else
                {
                    var status = scope.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        JobLog.Warn("ApplyBatch",
                            "Batch " + batchIndex + " of job " + job.JobId.ToString("D") +
                            " was rolled back by Revit (status " + status + ") " +
                            "[Action: none of the units in the batch were applied; inspect the failures and retry]");
                        MarkApplyingAsFailed(job, plans);
                        return new ApplyBatchOutcome
                        {
                            Changed = false,
                            Saved = false,
                            Error = new JobError(
                                "job_transaction_rolled_back",
                                "Revit rolled back the batch transaction (status " + status + ")",
                                "inspect per-unit failures in get_job_report and retry_failed_items")
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            scope?.RollBack();
            JobLog.Error("ApplyBatch",
                "Batch " + batchIndex + " apply failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: none of the units in the batch were applied; run get_job_report]");
            MarkApplyingAsFailed(job, plans);
            Sync(_store.CompleteBatchAsync(
                job.JobId, batchIndex, 0, plans.Count, false, null, CancellationToken.None));
            return new ApplyBatchOutcome
            {
                Changed = false,
                Saved = false,
                Error = new JobError(
                    "job_batch_failed",
                    "Batch transaction failed: " + ex.GetType().Name + ": " + ex.Message,
                    "run get_job_status; the job was stopped to avoid a partial batch")
            };
        }
        finally
        {
            scope?.Dispose();
        }

        if (abort)
        {
            MarkApplyingAsFailed(job, plans);
            Sync(_store.CompleteBatchAsync(
                job.JobId, batchIndex, 0, plans.Count, false, null, CancellationToken.None));
            Sync(_store.AppendEventAsync(
                job.JobId,
                JobEventTypes.BatchFailed,
                "{\"batch\":" + batchIndex + ",\"reason\":\"onError=abort\"}",
                CancellationToken.None));
            return new ApplyBatchOutcome
            {
                Changed = false,
                Saved = false,
                Error = new JobError(
                    "job_aborted_by_policy",
                    "A unit failed and onError=abort is set",
                    "run get_job_report to inspect the failure; the batch was rolled back")
            };
        }

        var updates = new List<JobItemUpdate>(plans.Count);
        var changed = false;

        foreach (var plan in plans)
        {
            if (plan.Error is not null)
            {
                updates.Add(new JobItemUpdate
                {
                    Seq = plan.Item.Seq,
                    State = JobItemState.Failed,
                    ErrorCode = plan.Error.Code,
                    ErrorDetail = plan.Error.ToMessage(),
                    BatchIndex = batchIndex
                });
                continue;
            }

            var result = applied.TryGetValue(plan.Item.Seq, out var value)
                ? value
                : new JobUnitApply { ErrorCode = JobErrorCodes.ItemFailed, ErrorDetail = "No apply result" };

            if (!result.Success)
            {
                updates.Add(new JobItemUpdate
                {
                    Seq = plan.Item.Seq,
                    State = JobItemState.Failed,
                    ErrorCode = result.ErrorCode ?? JobErrorCodes.ItemFailed,
                    ErrorDetail = result.ErrorDetail,
                    BatchIndex = batchIndex
                });
                continue;
            }

            JobUnitVerification verification;
            try
            {
                verification = action.Verify(context, ToUnit(plan.Item), plan.Preview);
            }
            catch (Exception ex)
            {
                verification = new JobUnitVerification
                {
                    Verified = false,
                    MismatchReason = ex.GetType().Name + ": " + ex.Message
                };
            }

            if (!verification.Verified)
            {
                updates.Add(new JobItemUpdate
                {
                    Seq = plan.Item.Seq,
                    State = JobItemState.Mismatch,
                    ActualJson = verification.ActualJson,
                    ErrorCode = JobErrorCodes.Mismatch,
                    ErrorDetail = verification.MismatchReason ?? "read-back mismatch",
                    BatchIndex = batchIndex
                });
                continue;
            }

            changed |= !result.Skipped && !plan.Preview.Skipped;
            updates.Add(new JobItemUpdate
            {
                Seq = plan.Item.Seq,
                State = result.Skipped || plan.Preview.Skipped ? JobItemState.Skipped : JobItemState.Done,
                ActualJson = verification.ActualJson,
                UndoJson = result.UndoJson ?? plan.Preview.UndoJson,
                ErrorCode = null,
                ErrorDetail = null,
                BatchIndex = batchIndex
            });
        }

        Sync(_store.UpdateItemsAsync(job.JobId, updates, CancellationToken.None));
        Sync(_store.UpdateTotalsAsync(
            job.JobId,
            JobJson.Serialize(JobTotals.FromItems(
                Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None)))),
            CancellationToken.None));

        var saved = false;
        JobError? saveError = null;

        if (JobSavePolicyDecider.ShouldSaveAfterBatch(job.Spec.Options.SavePolicy, changed) ||
            JobSavePolicyDecider.ShouldSaveOnTimer(
                job.Spec.Options.SavePolicy,
                job.LastSavedAt,
                DateTime.UtcNow,
                job.Spec.Options.SaveEveryMinutes))
        {
            saveError = SaveNow(job, document, toSeq);
            saved = saveError is null;
        }

        var failed = updates.Count(update => update.State is JobItemState.Failed or JobItemState.Mismatch);
        Sync(_store.CompleteBatchAsync(
            job.JobId,
            batchIndex,
            updates.Count - failed,
            failed,
            saved,
            saved ? DateTime.UtcNow : null,
            CancellationToken.None));

        if (saveError is not null)
        {
            return new ApplyBatchOutcome { Changed = changed, Saved = false, Error = saveError };
        }

        return new ApplyBatchOutcome { Changed = changed, Saved = saved };
    }

    private JobSliceResult FinalizeJob(
        JobRecord job,
        Document document,
        IJobAction action,
        int processed)
    {
        var context = CreateContext(document, job.Spec.DocumentId, job.JobId, job.Spec, dryRun: false);
        var items = Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None));

        // False-done guard: every unit that claims done/skipped is read back
        // independently one more time before the job may become completed.
        var corrections = new List<JobItemUpdate>();
        foreach (var item in items.Where(item => item.State is JobItemState.Done or JobItemState.Skipped))
        {
            var preview = JobJson.Deserialize<JobUnitPreview>(item.PreviewJson);
            if (preview is null)
            {
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

            if (!verification.Verified)
            {
                corrections.Add(new JobItemUpdate
                {
                    Seq = item.Seq,
                    State = JobItemState.Mismatch,
                    ActualJson = verification.ActualJson,
                    ErrorCode = JobErrorCodes.Mismatch,
                    ErrorDetail = verification.MismatchReason ?? "final read-back mismatch"
                });
            }
        }

        if (corrections.Count > 0)
        {
            Sync(_store.UpdateItemsAsync(job.JobId, corrections, CancellationToken.None));
            items = Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None));
        }

        var changed = items.Any(item => item.State is JobItemState.Done or JobItemState.Mismatch);
        if (changed)
        {
            var saveError = SaveNow(job, document, LastSeqOf(job));
            if (saveError is not null)
            {
                _wantsPump = false;
                TryFail(job, saveError.Code, saveError.ToMessage());
                return new JobSliceResult
                {
                    JobId = job.JobId,
                    State = JobState.Failed,
                    ProcessedThisSlice = processed,
                    Error = saveError
                };
            }
        }

        var totals = JobTotals.FromItems(items);
        Sync(_store.UpdateTotalsAsync(job.JobId, JobJson.Serialize(totals), CancellationToken.None));

        var finalState = totals.Mismatch > 0 || totals.Failed > 0 || totals.UndoFailed > 0
                         || totals.Incomplete > 0
            ? JobState.CompletedWithFailures
            : JobState.Completed;

        var transition = Sync(_store.TransitionAsync(
            job.JobId,
            JobState.Running,
            finalState,
            finalState == JobState.CompletedWithFailures ? JobErrorCodes.ItemFailed : null,
            finalState == JobState.CompletedWithFailures
                ? "failed=" + totals.Failed + ", mismatch=" + totals.Mismatch +
                  ", incomplete=" + totals.Incomplete
                : null,
            CancellationToken.None));

        if (transition is not null)
        {
            var latest = Sync(_store.GetAsync(job.JobId, CancellationToken.None));
            if (latest is not null && latest.State == JobState.Cancelling)
            {
                return new JobSliceResult
                {
                    JobId = job.JobId,
                    State = JobState.Cancelling,
                    HasMoreWork = true,
                    ProcessedThisSlice = processed
                };
            }

            if (latest is not null && JobStateMachine.IsTerminal(latest.State))
            {
                return new JobSliceResult
                {
                    JobId = job.JobId,
                    State = latest.State,
                    HasMoreWork = false,
                    ProcessedThisSlice = processed
                };
            }

            TryFail(job, transition.Code, transition.Message);
            return new JobSliceResult
            {
                JobId = job.JobId,
                State = JobState.Failed,
                ProcessedThisSlice = processed,
                Error = transition
            };
        }

        Sync(_store.MarkFinishedAsync(
            job.JobId,
            finalState,
            finalState == JobState.CompletedWithFailures ? JobErrorCodes.ItemFailed : null,
            finalState == JobState.CompletedWithFailures
                ? "failed=" + totals.Failed + ", mismatch=" + totals.Mismatch
                : null,
            CancellationToken.None));

        Sync(_store.AppendEventAsync(
            job.JobId,
            finalState == JobState.Completed
                ? JobEventTypes.Completed
                : JobEventTypes.CompletedWithFailures,
            "{\"done\":" + totals.Done + ",\"skipped\":" + totals.Skipped +
            ",\"failed\":" + totals.Failed + ",\"mismatch\":" + totals.Mismatch + "}",
            CancellationToken.None));

        _wantsPump = false;
        return new JobSliceResult
        {
            JobId = job.JobId,
            State = finalState,
            HasMoreWork = false,
            ProcessedThisSlice = processed
        };
    }

    private JobError? SaveNow(JobRecord job, Document document, int cursor)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", "SaveNow"));

        if (job.Spec.Options.SavePolicy == JobSavePolicy.Never)
        {
            return null;
        }

        try
        {
            if (document.IsModified)
            {
                document.Save();
            }

            Sync(_store.UpdateCursorAsync(job.JobId, cursor, DateTime.UtcNow, CancellationToken.None));
            return null;
        }
        catch (Exception ex)
        {
            var error = JobErrors.SaveFailed(ex.GetType().Name + ": " + ex.Message);
            Sync(_store.AppendEventAsync(
                job.JobId,
                JobEventTypes.SaveFailed,
                "{\"cursor\":" + cursor + ",\"error\":\"" + ex.GetType().Name + "\"}",
                CancellationToken.None));
            SmartConLogger.Error(
                "Save failed for job " + job.JobId.ToString("D") + ": " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: save the document manually, then resume the job]");
            return error;
        }
    }

    private void PersistUndoIntent(JobRecord job, int seq, string undoJson)
    {
        if (string.IsNullOrWhiteSpace(undoJson))
        {
            return;
        }

        Sync(_store.UpdateItemsAsync(
            job.JobId,
            new[]
            {
                new JobItemUpdate
                {
                    Seq = seq,
                    State = JobItemState.Applying,
                    UndoJson = undoJson
                }
            },
            CancellationToken.None));
    }
}
