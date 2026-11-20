using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>Per-unit undo loop shared by cancel-with-rollback and explicit rollback.</summary>
public sealed partial class JobRunner
{
    /// <summary>
    /// Undo loop shared by cancel-with-rollback and explicit rollback. Processes
    /// units in slices; each batch is one transaction for project actions, while
    /// family units run one per slice (EditFamily + LoadFamily is heavy).
    /// </summary>
    private UndoOutcome RunUndoBatches(
        JobRecord job,
        Document document,
        TimeSpan budget,
        long started,
        bool singleBatchPerSlice)
    {
        var action = _actions[job.Spec.Action];
        var context = CreateContext(document, job.Spec.DocumentId, job.JobId, job.Spec, dryRun: false);
        var batchSize = EffectiveBatchSize(job, action);

        // In pump mode (cancel-with-rollback) a failed unit is not recycled: that
        // would spin the pump forever after a commit that Revit keeps rolling back.
        var resetStates = singleBatchPerSlice
            ? new[] { JobItemState.Done, JobItemState.Mismatch, JobItemState.Applying, JobItemState.Undoing }
            : new[]
            {
                JobItemState.Done, JobItemState.Mismatch, JobItemState.Applying,
                JobItemState.Undoing, JobItemState.UndoFailed
            };

        var applied = Sync(_store.LoadItemsAsync(
            job.JobId,
            resetStates,
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
            var batchRestored = 0;
            var batchSkipped = 0;
            var batchFailed = 0;
            var committed = true;
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
                        batchSkipped++;
                        updates.Add(new JobItemUpdate
                        {
                            Seq = item.Seq,
                            State = JobItemState.Undone,
                            ActualJson = undo.ActualJson
                        });
                    }
                    else if (undo.Success)
                    {
                        batchRestored++;
                        updates.Add(new JobItemUpdate
                        {
                            Seq = item.Seq,
                            State = JobItemState.Undone,
                            ActualJson = undo.ActualJson
                        });
                    }
                    else
                    {
                        batchFailed++;
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
                        committed = false;
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

                        batchRestored = 0;
                        batchSkipped = 0;
                        batchFailed = items.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                scope?.RollBack();
                committed = false;
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

                batchRestored = 0;
                batchSkipped = 0;
                batchFailed = items.Count;

                JobLog.Error("RunUndoBatches",
                    "Rollback batch failed: " + ex.GetType().Name + ": " + ex.Message +
                    " [Action: run rollback_job again or restore the model from the backup file]");
            }
            finally
            {
                scope?.Dispose();
            }

            restored += batchRestored;
            skipped += batchSkipped;
            undoFailed += batchFailed;

            if (updates.Count > 0)
            {
                Sync(_store.UpdateItemsAsync(job.JobId, updates, CancellationToken.None));
            }

            if (singleBatchPerSlice || !committed)
            {
                break;
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