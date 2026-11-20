namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>
/// Typed job error with an actionable hint. Every error code has exactly one
/// meaning, and every message ends with <c>[Action: ...]</c> so the model and the
/// operator know the next step (L9).
/// </summary>
public sealed class JobError
{
    public JobError(string code, string message, string action)
    {
        Code = code;
        Message = message;
        Action = action;
    }

    public string Code { get; }

    public string Message { get; }

    public string Action { get; }

    public string ToMessage() => Message + " [Action: " + Action + "]";

    public override string ToString() => Code + ": " + ToMessage();
}

public static class JobErrorCodes
{
    public const string JobNotFound = "job_not_found";
    public const string JobAlreadyActive = "job_already_active";
    public const string DocumentMismatch = "job_document_mismatch";
    public const string DocumentChanged = "job_document_changed";
    public const string DocumentNotSaved = "job_document_not_saved";
    public const string DocumentReadOnly = "job_document_read_only";
    public const string WorksharedUnsupported = "job_workshared_unsupported";
    public const string InvalidState = "job_invalid_state";
    public const string InvalidSpec = "job_spec_invalid";
    public const string ActionUnknown = "job_action_unknown";
    public const string PreviewRequired = "job_preview_required";
    public const string PreviewMismatch = "job_preview_mismatch";
    public const string PreviewExpired = "job_preview_expired";
    public const string PreviewStale = "job_preview_stale";
    public const string PreviewSuperseded = "job_preview_superseded";
    public const string SelectorEmpty = "job_selector_empty";
    public const string LimitExceeded = "job_limit_exceeded";
    public const string ConfirmationRequired = "job_confirmation_required";
    public const string AbortedByPolicy = "job_aborted_by_policy";
    public const string SaveFailed = "job_save_failed";
    public const string CancelledByUser = "job_cancelled";
    public const string Interrupted = "job_interrupted";
    public const string RollbackUnavailable = "job_rollback_unavailable";
    public const string RollbackFailed = "job_rollback_failed";
    public const string ItemFailed = "job_item_failed";
    public const string Mismatch = "job_mismatch";
    public const string Timeout = "job_timeout";
    public const string Busy = "job_busy";
}

public static class JobErrors
{
    public static JobError InvalidState(string detail, string action) =>
        new(JobErrorCodes.InvalidState, "Invalid job state: " + detail, action);

    public static JobError Spec(string detail) =>
        new(JobErrorCodes.InvalidSpec, "Invalid job spec: " + detail,
            "fix the arguments and call start_job again; action-specific specs list their allowed keys in the error");

    public static JobError ActionUnknown(string action) =>
        new(JobErrorCodes.ActionUnknown, "Unknown job action '" + action + "'",
            "call find_tools or list the jobs pack to see supported actions");

    public static JobError AlreadyActive(string activeJobId) =>
        new(JobErrorCodes.JobAlreadyActive,
            "Another job is already active on this document: " + activeJobId,
            "wait for it (get_job_status) or cancel it (cancel_job) before starting a new one");

    public static JobError DocumentMismatch(string expected, string actual) =>
        new(JobErrorCodes.DocumentMismatch,
            "Job belongs to document '" + expected + "', but the active document is '" + actual + "'",
            "open the document the job was started in, or start a new job in the active document");

    public static JobError Preview(string detail, string action) =>
        new(JobErrorCodes.PreviewRequired, "Preview required: " + detail, action);

    public static JobError PreviewMismatch() =>
        new(JobErrorCodes.PreviewMismatch,
            "The preview digest does not match the frozen preview of this job",
            "run start_job with dryRun=true again and apply using the returned previewDigest");

    public static JobError PreviewExpired(DateTime expiresAtUtc) =>
        new(JobErrorCodes.PreviewExpired,
            "The preview expired at " + expiresAtUtc.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
            "run start_job with dryRun=true again; the model may have changed since the preview");

    public static JobError PreviewStale(string unitKey) =>
        new(JobErrorCodes.PreviewStale,
            "Unit '" + unitKey + "' changed in the model after the preview",
            "run the preview again; applying a stale preview is refused to avoid overwriting newer changes");

    public static JobError SelectorEmpty(string action) =>
        new(JobErrorCodes.SelectorEmpty,
            "The selector of action '" + action + "' matched no units",
            "broaden the selector (widen name/category filters) and run the preview again");

    public static JobError LimitExceeded(int count, int limit) =>
        new(JobErrorCodes.LimitExceeded,
            "Job would touch " + count + " units, which exceeds the limit of " + limit,
            "narrow the selector or raise the limit in smaller batches; no changes were made");

    public static JobError LargeJobConfirmation(int count, int threshold) =>
        new(JobErrorCodes.ConfirmationRequired,
            "This job touches " + count + " units, above the " + threshold +
            "-unit safety threshold, and was not acknowledged",
            "review the preview, then call start_job with dryRun=false, the same previewDigest " +
            "and allowLargeJobs=true to apply it");

    public static JobError AbortedByPolicy(string detail) =>
        new(JobErrorCodes.AbortedByPolicy,
            "The job was stopped by onError=abort: " + detail,
            "inspect get_job_report; fix the failing units and run a fresh preview");

    public static JobError SaveFailed(string detail) =>
        new(JobErrorCodes.SaveFailed,
            "Document.Save() failed: " + detail,
            "save the document manually (File > Save) and resume the job; applied changes stay in memory only");

    public static JobError DocumentNotSaved() =>
        new(JobErrorCodes.DocumentNotSaved,
            "The document has never been saved, so progress cannot be made durable",
            "save the project with File > Save As first, then start the job again");

    public static JobError DocumentReadOnly() =>
        new(JobErrorCodes.DocumentReadOnly,
            "The document file is read-only, so progress cannot be made durable",
            "save a writable copy (File > Save As) and start the job in that copy");

    public static JobError Workshared() =>
        new(JobErrorCodes.WorksharedUnsupported,
            "Workshared documents are not supported by the job engine yet",
            "detach the model (or work in a non-workshared copy) and start the job again");

    public static JobError DocumentChanged(string expected, string actual) =>
        new(JobErrorCodes.DocumentChanged,
            "The active document changed during the job ('" + expected + "' -> '" + actual + "')",
            "the job was stopped to avoid writing to the wrong model; run get_job_status and resume in the right document");

    public static JobError Cancelled() =>
        new(JobErrorCodes.CancelledByUser, "The job was cancelled by the user",
            "run get_job_report to see what was applied; call rollback_job to restore the model");

    public static JobError ItemFailed(string unitKey, string detail) =>
        new(JobErrorCodes.ItemFailed,
            "Unit '" + unitKey + "' failed: " + detail,
            "run get_job_report to inspect failed items and retry_failed_items after fixing the cause");

    public static JobError Mismatch(string unitKey, string detail) =>
        new(JobErrorCodes.Mismatch,
            "Unit '" + unitKey + "' did not verify: " + detail,
            "run get_job_report; rollback_job restores verified undo data, then retry");

    public static JobError RollbackUnavailable(string detail) =>
        new(JobErrorCodes.RollbackUnavailable,
            "Rollback is unavailable: " + detail,
            "restore the model from the backup file or undo manually; the job has no per-item undo for these units");

    public static JobError RollbackFailed(string detail) =>
        new(JobErrorCodes.RollbackFailed,
            "Rollback partially failed: " + detail,
            "run rollback_job again for the failed items or restore from the backup file");

    public static JobError Timeout(int seconds) =>
        new(JobErrorCodes.Timeout,
            "The job did not advance for " + seconds + " seconds",
            "cancel the job (cancel_job) and run get_job_report; then retry or resume");

    public static JobError Busy() =>
        new(JobErrorCodes.Busy, "The job engine is busy with another slice",
            "wait a moment and call get_job_status again");
}