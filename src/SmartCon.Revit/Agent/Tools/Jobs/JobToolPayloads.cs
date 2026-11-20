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

    internal static object Describe(
        JobRecord job,
        bool includeSample = false,
        object? recentJobs = null)
    {
        var preview = Preview(job);
        var totals = Totals(job);

        return new
        {
            jobId = job.JobId.ToString("D"),
            action = job.Spec.Action,
            state = job.State.ToString(),
            mode = ModeOf(job),
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
            failed = job.StartedAt is null ? preview?.FailedCount : totals.Failed,
            stale = totals.Stale,
            done = totals.Done,
            mismatch = totals.Mismatch,
            undone = totals.Undone,
            undoFailed = totals.UndoFailed,
            appliedBefore = totals.AppliedBefore,
            cancelled = job.CancelRequested,
            interruptedUnits = job.CancelRequested ? totals.Pending + totals.Previewed : 0,
            previewDigest = preview?.Digest,
            previewExpiresAtUtc = preview?.ExpiresAtUtc,
            sample = includeSample ? preview?.Sample : null,
            warnings = preview?.Warnings,
            recentJobs,
            next = NextHint(job)
        };
    }

    internal static object[] History(IReadOnlyList<JobRecord> jobs) =>
        jobs.Select(job => new
        {
            jobId = job.JobId.ToString("D"),
            action = job.Spec.Action,
            state = job.State.ToString(),
            cancelled = job.CancelRequested,
            createdAt = job.CreatedAt,
            finishedAt = job.FinishedAt
        }).ToArray();

    /// <summary>
    /// Truthful phase: a preview job that was applied stores Mode=Preview, so the
    /// raw enum contradicts the lifecycle. Derive the phase from state + StartedAt.
    /// </summary>
    internal static string ModeOf(JobRecord job)
    {
        if (job.State is JobState.PreviewRunning or JobState.PreviewReady or JobState.AwaitingApproval)
        {
            return JobMode.Preview.ToString();
        }

        if (job.Spec.Mode == JobMode.Preview && job.StartedAt is null)
        {
            return JobMode.Preview.ToString();
        }

        return job.Spec.Mode == JobMode.Resume
            ? JobMode.Resume.ToString()
            : JobMode.Apply.ToString();
    }

    internal static string NextHint(JobRecord job)
    {
        if (job.ErrorCode == JobErrorCodes.PreviewSuperseded)
        {
            return "preview superseded: nothing was applied; run start_job with dryRun=true again " +
                   "using the new previewDigest";
        }

        if (Totals(job).Stale > 0 &&
            job.State is JobState.CompletedWithFailures or JobState.Failed)
        {
            return "stale preview: " + Totals(job).Stale +
                   " unit(s) changed after the preview; run start_job with dryRun=true again " +
                   "and apply the fresh previewDigest";
        }

        return job.State switch
        {
            JobState.PreviewRunning => "wait: call get_job_status (waitSeconds=10) until state=PreviewReady",
        JobState.PreviewReady =>
            "apply: call start_job with dryRun=false and previewDigest=" +
            (Preview(job)?.Digest ?? "<digest>") +
            ((Preview(job)?.Count ?? 0) > JobOptions.LargeJobThreshold
                ? " plus allowLargeJobs=true (job above " + JobOptions.LargeJobThreshold + " units)"
                : string.Empty),
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
    }

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
        if (string.IsNullOrWhiteSpace(text) ||
            string.Equals(text, "last", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "latest", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "recent", StringComparison.OrdinalIgnoreCase))
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
