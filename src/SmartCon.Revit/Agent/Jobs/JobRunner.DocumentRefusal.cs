using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>
/// Document-level refusals and the durability helpers of the job runner.
/// </summary>
public sealed partial class JobRunner
{
    /// <summary>
    /// A refusal by the document (project read-only, another transaction open) is
    /// not a property of one unit: every following unit of the job is refused the
    /// same way. The runner stops the job with one error that names the cause
    /// instead of reporting N identical unit failures; the units that were not
    /// attempted keep their state.
    /// </summary>
    private static bool IsDocumentRefusal(string? errorCode) =>
        string.Equals(errorCode, AgentFamilySession.PermissionDenied, StringComparison.Ordinal);

    private JobError StopForDocumentRefusal(JobRecord job, DocumentRefusalInfo refusal, bool applying)
    {
        var items = Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None));
        var error = DocumentRefusal(refusal, applying, rolledBack: false, items);

        WarnDocumentRefusal(job, refusal);

        _wantsPump = false;
        TryFail(job, error.Code, error.ToMessage());
        Sync(_store.UpdateTotalsAsync(
            job.JobId,
            JobJson.Serialize(JobTotals.FromItems(items)),
            CancellationToken.None));

        return error;
    }

    /// <summary>
    /// rollbackOnError after a document refusal in apply: the applied units are
    /// undone first and the rolled-back job is finalized with the refusal as its
    /// cause. Finalizing before the undo left the job Failed while its units were
    /// in fact restored.
    /// </summary>
    private JobSliceResult RollbackAfterDocumentRefusal(
        JobRecord job,
        Document document,
        DocumentRefusalInfo refusal,
        int processed)
    {
        WarnDocumentRefusal(job, refusal);
        var cause = DocumentRefusal(
            refusal,
            applying: true,
            rolledBack: true,
            Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None)));

        return RollbackAfterError(job, document, cause, processed);
    }

    private static void WarnDocumentRefusal(JobRecord job, DocumentRefusalInfo refusal) =>
        JobLog.Warn(
            "DocumentRefusal",
            "Job " + job.JobId.ToString("D") + " stopped: the document refused unit '" +
            (refusal.Item.Label ?? refusal.Item.UnitKey) + "' (" + refusal.Detail + ") " +
            "[Action: make the document accept edits again, then run the job again]");

    /// <summary>
    /// The job-level error of a document refusal. A rolled-back job no longer
    /// has applied units, so it names only what was not attempted.
    /// </summary>
    private static JobError DocumentRefusal(
        DocumentRefusalInfo refusal,
        bool applying,
        bool rolledBack,
        IReadOnlyList<JobItem> items)
    {
        // Preview phase: only Pending units were not reached. Apply phase: units
        // that passed the preview but were not applied yet count as not attempted.
        var notAttempted = applying
            ? items.Count(state => state.State is JobItemState.Previewed or JobItemState.Pending)
            : items.Count(state => state.State == JobItemState.Pending);
        var applied = items.Count(state => state.State == JobItemState.Done);
        var phase = applying ? "applied" : "previewed";

        return new JobError(
            JobErrorCodes.DocumentReadOnly,
            "The project document stopped accepting edits while unit '" +
            (refusal.Item.Label ?? refusal.Item.UnitKey) + "' was being " + phase + " (" + refusal.Detail + "). " +
            (rolledBack
                ? notAttempted + " unit(s) were not attempted."
                : (applied > 0
                    ? applied + " unit(s) applied before it stay applied; "
                    : "Nothing was applied; ") +
                  notAttempted + " unit(s) were not attempted."),
            rolledBack || applied == 0
                ? "make the project accept edits again, then run start_job with dryRun=true again"
                : "make the project accept edits again, then resume with start_job (resumeJobId) " +
                  "or undo with rollback_job");
    }

    private sealed record DocumentRefusalInfo(JobItem Item, string? Detail);

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
