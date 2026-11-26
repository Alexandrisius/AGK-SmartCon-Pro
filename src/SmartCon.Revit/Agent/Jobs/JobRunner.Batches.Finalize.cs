using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>Batch finalization and verification of the job runner.</summary>
public sealed partial class JobRunner
{
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
        // A worksharing skip carries an ErrorDetail (the element was not written),
        // so it is not a false-done claim and is not re-verified.
        var corrections = new List<JobItemUpdate>();
        foreach (var item in items.Where(item =>
                     item.State is JobItemState.Done or JobItemState.Skipped
                     && string.IsNullOrEmpty(item.ErrorDetail)))
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

            if (verification.Verified)
            {
                // #432: keep the stored not-verifiable count in step with the final
                // read-back; the state itself stays Done/Skipped.
                if (verification.NotVerifiable != item.NotVerifiable)
                {
                    corrections.Add(new JobItemUpdate
                    {
                        Seq = item.Seq,
                        State = item.State,
                        NotVerifiable = verification.NotVerifiable,
                        ActualJson = verification.ActualJson
                    });
                }

                continue;
            }

            corrections.Add(new JobItemUpdate
            {
                Seq = item.Seq,
                State = JobItemState.Mismatch,
                ActualJson = verification.ActualJson,
                ErrorCode = JobErrorCodes.Mismatch,
                ErrorDetail = verification.MismatchReason ?? "final read-back mismatch"
            });
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

        var finalState = JobFinalStateDecider.Decide(totals);

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
                : finalState == JobState.CompletedUnverified
                    ? JobEventTypes.CompletedUnverified
                    : JobEventTypes.CompletedWithFailures,
            "{\"done\":" + totals.Done + ",\"skipped\":" + totals.Skipped +
            ",\"failed\":" + totals.Failed + ",\"mismatch\":" + totals.Mismatch +
            ",\"notVerifiable\":" + totals.NotVerifiable + "}",
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
}
