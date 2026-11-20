using System.Diagnostics;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>rollbackOnError: undo applied units after an apply error.</summary>
public sealed partial class JobRunner
{
    /// <summary>
    /// rollbackOnError: a failed/aborted apply undoes everything that was already
    /// applied, so the run never leaves a half-finished model behind.
    /// </summary>
    private JobSliceResult RollbackAfterError(
        JobRecord job,
        Document document,
        JobError cause,
        int processed)
    {
        Sync(_store.AppendEventAsync(
            job.JobId, JobEventTypes.RollbackStarted, null, CancellationToken.None));
        JobLog.Info("RollbackAfterError",
            "Job " + job.JobId.ToString("D") + " applies rollbackOnError after " + cause.Code);

        var outcome = RunUndoBatches(
            job,
            document,
            TimeSpan.FromSeconds(45),
            Stopwatch.GetTimestamp(),
            singleBatchPerSlice: false);

        var hasProblems = outcome.UndoFailed > 0 || outcome.SaveError is not null;
        var target = outcome.HasMoreWork || hasProblems
            ? JobState.RollbackPartial
            : JobState.RolledBack;

        var transition = Sync(_store.TransitionAsync(
            job.JobId,
            JobState.Running,
            target,
            hasProblems ? JobErrorCodes.RollbackFailed : cause.Code,
            hasProblems
                ? outcome.UndoFailed + " unit(s) could not be restored after: " + cause.ToMessage()
                : "onError/rollbackOnError restored " + outcome.Restored + " unit(s) after: " + cause.Message,
            CancellationToken.None));

        if (transition is not null)
        {
            _wantsPump = false;
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
            target,
            hasProblems ? JobErrorCodes.RollbackFailed : cause.Code,
            hasProblems
                ? outcome.UndoFailed + " unit(s) could not be restored"
                : "Rolled back " + outcome.Restored + " unit(s) after " + cause.Code,
            CancellationToken.None));
        Sync(_store.AppendEventAsync(
            job.JobId,
            JobEventTypes.RollbackCompleted,
            "{\"restored\":" + outcome.Restored + ",\"failed\":" + outcome.UndoFailed + "}",
            CancellationToken.None));
        Sync(_store.UpdateTotalsAsync(
            job.JobId,
            JobJson.Serialize(JobTotals.FromItems(
                Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None)))),
            CancellationToken.None));

        _wantsPump = false;
        return new JobSliceResult
        {
            JobId = job.JobId,
            State = target,
            HasMoreWork = false,
            ProcessedThisSlice = processed,
            Error = hasProblems
                ? JobErrors.RollbackFailed(
                    outcome.UndoFailed + " unit(s) could not be restored")
                : cause
        };
    }
}
