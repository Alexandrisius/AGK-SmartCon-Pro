using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>onError=abort: honest per-unit state and rollback policy.</summary>
public sealed partial class JobRunner
{
    /// <summary>
    /// Honest per-unit state after onError=abort. A transaction batch rolled back
    /// entirely; family units have no transaction, so units that were already
    /// applied are verified and kept (rollbackOnError may undo them later).
    /// </summary>
    private List<JobItemUpdate> BuildAbortUpdates(
        IJobAction action,
        JobActionContext context,
        IReadOnlyList<BatchPlan> plans,
        IReadOnlyDictionary<int, JobUnitApply> applied,
        int batchIndex,
        bool transactionScope)
    {
        var updates = new List<JobItemUpdate>(plans.Count);
        foreach (var plan in plans)
        {
            if (transactionScope)
            {
                if (plan.Error is null && plan.Preview.Skipped)
                {
                    // Already at the target: a rollback of the batch does not
                    // invalidate a unit that was never going to be written.
                    updates.Add(new JobItemUpdate
                    {
                        Seq = plan.Item.Seq,
                        State = JobItemState.Skipped,
                        BatchIndex = batchIndex
                    });
                    continue;
                }

                updates.Add(new JobItemUpdate
                {
                    Seq = plan.Item.Seq,
                    State = JobItemState.Failed,
                    ErrorCode = plan.Error?.Code ?? JobErrorCodes.AbortedByPolicy,
                    ErrorDetail = plan.Error?.ToMessage() ??
                                  "onError=abort stopped the batch; the transaction was rolled back",
                    BatchIndex = batchIndex
                });
                continue;
            }

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

            if (!applied.TryGetValue(plan.Item.Seq, out var result))
            {
                updates.Add(new JobItemUpdate
                {
                    Seq = plan.Item.Seq,
                    State = JobItemState.Failed,
                    ErrorCode = JobErrorCodes.AbortedByPolicy,
                    ErrorDetail = "onError=abort stopped the job before this unit was applied",
                    BatchIndex = batchIndex
                });
                continue;
            }

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
                    ErrorDetail = verification.MismatchReason ?? "read-back mismatch after abort",
                    BatchIndex = batchIndex
                });
                continue;
            }

            updates.Add(new JobItemUpdate
            {
                Seq = plan.Item.Seq,
                State = result.Skipped || plan.Preview.Skipped
                    ? JobItemState.Skipped
                    : JobItemState.Done,
                ActualJson = verification.ActualJson,
                UndoJson = result.UndoJson ?? plan.Preview.UndoJson,
                BatchIndex = batchIndex
            });
        }

        return updates;
    }

}
