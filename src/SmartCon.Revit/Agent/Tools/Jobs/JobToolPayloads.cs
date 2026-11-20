using System.Globalization;
using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Jobs;

/// <summary>Shared payloads and lookups for the job tools.</summary>
internal static class JobToolPayloads
{
    internal static JobPreview? Preview(JobRecord job) => JobJson.Deserialize<JobPreview>(job.PreviewJson);

    internal static JobTotals Totals(JobRecord job)
    {
        var totals = JobJson.Deserialize<JobTotals>(job.TotalsJson);
        return totals ?? new JobTotals();
    }

    internal static JsonElement Serialize(object payload) => AgentJson.Serialize(payload);

    internal static object Describe(JobRecord job)
    {
        var preview = Preview(job);
        var totals = Totals(job);

        return new
        {
            jobId = job.JobId.ToString("D"),
            action = job.Spec.Action,
            state = job.State.ToString(),
            mode = job.Spec.Mode.ToString(),
            saved = job.LastSavedCursor > 0 && job.LastSavedAt is not null,
            volatileJob = job.Spec.Options.SavePolicy == JobSavePolicy.Never,
            lastSavedCursor = job.LastSavedCursor,
            lastSavedAt = job.LastSavedAt,
            createdAt = job.CreatedAt,
            updatedAt = job.UpdatedAt,
            finishedAt = job.FinishedAt,
            errorCode = job.ErrorCode,
            errorDetail = job.ErrorDetail,
            count = preview?.Count ?? totals.Total,
            willChange = preview?.WillChangeCount,
            skipped = preview?.SkipCount,
            failed = preview?.FailedCount,
            done = totals.Done,
            mismatch = totals.Mismatch,
            undone = totals.Undone,
            undoFailed = totals.UndoFailed,
            previewDigest = preview?.Digest,
            previewExpiresAtUtc = preview?.ExpiresAtUtc,
            sample = preview?.Sample,
            warnings = preview?.Warnings,
            next = NextHint(job)
        };
    }

    internal static string NextHint(JobRecord job) => job.State switch
    {
        JobState.PreviewRunning => "wait: call get_job_status (waitSeconds=10) until state=PreviewReady",
        JobState.PreviewReady =>
            "apply: call start_job with dryRun=false, same action/spec and previewDigest=" +
            (Preview(job)?.Digest ?? "<digest>"),
        JobState.Running => "wait: call get_job_status (waitSeconds=10) until a terminal state",
        JobState.Cancelling => "wait: the runner is stopping between units",
        JobState.Interrupted =>
            "resume: call start_job with action, resumeJobId, dryRun=false",
        JobState.Completed => "report: call get_job_report to get the per-item diff",
        JobState.CompletedWithFailures =>
            "report: call get_job_report; then retry_failed_items or rollback_job",
        JobState.Cancelled => "report: call get_job_report; then rollback_job if needed",
        JobState.RolledBack => "nothing to do: the model was restored",
        JobState.RollbackPartial =>
            "inspect: call get_job_report; run rollback_job again for the failed units",
        JobState.Failed => "inspect: call get_job_report and fix the cause before retrying",
        _ => "call get_job_status"
    };

    /// <summary>
    /// Strict jobId reader: an absent/empty id means "use the most recent job",
    /// but a non-empty malformed id is an error — silently falling back to another
    /// job would let rollback/retry hit the wrong target.
    /// </summary>
    internal static bool TryReadJobId(JsonElement args, out Guid jobId, out ToolResult? error)
    {
        jobId = Guid.Empty;
        error = null;

        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("jobId", out var value))
        {
            return false;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (Guid.TryParse(text, out jobId))
        {
            return true;
        }

        error = ToolResult.Fail(
            "invalid_arguments",
            "jobId '" + text + "' is not a valid job GUID. " +
            "[Action: pass the jobId returned by start_job, or omit it to use the most recent job]");
        return false;
    }

}
