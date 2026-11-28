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
        object? recentJobs = null,
        IReadOnlyList<JobItem>? failedItems = null,
        IReadOnlyList<JobItem>? skippedItems = null,
        bool includeWarnings = true)
    {
        var preview = Preview(job);
        var totals = Totals(job);
        var mode = ModeOf(job);

        return new
        {
            jobId = job.JobId.ToString("D"),
            action = job.Spec.Action,
            state = job.State.ToString(),
            mode,
            // The apply call returns before the pump finishes: without an explicit
            // flag the model reported "52/52" while the job was 50/52 Running (B6).
            mustVerify = !JobStateMachine.IsTerminal(job.State),
            revertWith = mode == JobMode.Preview.ToString()
                ? null
                : "rollback_job(jobId=" + job.JobId.ToString("D") + ")",
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
            // A preview stopped before its envelope (document refusal) is counted by the totals.
            failed = job.StartedAt is null ? preview?.FailedCount ?? totals.Failed : totals.Failed,
            stale = totals.Stale,
            done = totals.Done,
            mismatch = totals.Mismatch,
            // #432: applied targets no project-side read can verify; separate from
            // mismatch ("unknown" is not "wrong").
            notVerified = totals.NotVerifiable,
            undone = totals.Undone,
            undoFailed = totals.UndoFailed,
            appliedBefore = totals.AppliedBefore,
            cancelled = job.CancelRequested,
            // "Cancelled" without a cancel request confused the live agent: a
            // superseded preview is not a user cancellation.
            superseded = job.State == JobState.Cancelled
                         && job.ErrorCode == JobErrorCodes.PreviewSuperseded,
            supersededReason = job.ErrorCode == JobErrorCodes.PreviewSuperseded
                ? job.ErrorDetail
                : null,
            // ETA: the live agent could not tell "working" from "hung" on 200+
            // units. Rate/estimate appear once at least one unit settled; a
            // "0 units in N minutes" answer is itself a diagnosis.
            elapsedMs = job.StartedAt.HasValue
                ? (int)((job.FinishedAt ?? job.UpdatedAt) - job.StartedAt.Value).TotalMilliseconds
                : (int?)(null),
            unitsPerMinute = PaceOf(job, totals),
            interruptedUnits = job.CancelRequested ? totals.Pending + totals.Previewed : 0,
            previewDigest = preview?.Digest,
            previewExpiresAtUtc = preview?.ExpiresAtUtc,
            sample = includeSample ? preview?.Sample : null,
            // Failure causes are part of every status: grouped by the code of the
            // CAUSE (the unit's errorCode), so get_job_report is not needed to
            // learn why units failed.
            failureCauses = FailureCauses(failedItems),
            // Why units were skipped: "2 skipped" without a reason cost a
            // get_job_report round just to learn it was duplicates (S.47, #532).
            skippedByReason = SkippedReasons(skippedItems),
            failedUnits = includeSample ? FailedUnits(failedItems, MaxFailedUnits) : null,
            // Long-poll statuses no longer re-emit the full per-unit warning
            // list (live v18: 17 mapping skips duplicated in every status
            // answer). The STATUS payload carries the count plus the
            // structural formula aggregate; the full list stays one
            // get_job_report(includeItems=true) away.
            warningCount = preview?.Warnings.Count,
            formulaDrivenSources = preview?.FormulaDrivenSources,
            warnings = includeWarnings ? preview?.Warnings : null,
            recentJobs,
            next = NextHint(job)
        };
    }

    internal static double? PaceOf(JobRecord job, JobTotals totals)
    {
        if (job.StartedAt is null)
            return null;

        var elapsed = (job.FinishedAt ?? job.UpdatedAt) - job.StartedAt.Value;
        var settled = totals.Done + totals.Failed + totals.Mismatch;
        if (elapsed.TotalMinutes < 0.2 || settled <= 0)
            return null;

        return Math.Round(settled / elapsed.TotalMinutes, 2);
    }

    internal static object[] History(IReadOnlyList<JobRecord> jobs) =>
        jobs.Select(job => new
        {
            jobId = job.JobId.ToString("D"),
            action = job.Spec.Action,
            state = job.State.ToString(),
            cancelled = job.CancelRequested,
            superseded = job.State == JobState.Cancelled
                         && job.ErrorCode == JobErrorCodes.PreviewSuperseded,
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

        if (job.ErrorCode == JobErrorCodes.DocumentReadOnly)
        {
            var makeEditable = "make it accept edits again (close the " +
                               "Family Editor and other pending operations, or reload the project), then ";
            if (job.State == JobState.RolledBack)
            {
                return "the project document refused edits and the applied units were rolled back; " +
                       makeEditable + "run start_job with dryRun=true again";
            }

            if (job.State == JobState.RollbackPartial)
            {
                return "the project document refused edits and the rollback is incomplete; " +
                       makeEditable + "run rollback_job again for the units that could not be restored";
            }

            return "the project document refused edits; " + makeEditable +
                   "run start_job with dryRun=true again (resumeJobId when units were applied)";
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
        JobState.PreviewReady => PreviewReadyHint(job),
        JobState.Running => "wait: call get_job_status (waitSeconds=10) until a terminal state",
        JobState.Cancelling => "wait: the runner is stopping between units",
        JobState.Interrupted =>
            "resume: call start_job with action, resumeJobId, dryRun=false",
            JobState.Completed => "report: call get_job_report to get the per-item diff",
            JobState.CompletedUnverified =>
                "applied; " + Totals(job).NotVerifiable +
                " target(s) not verifiable from the project (instance/element refs) — nothing to " +
                "retry; rollback_job if unwanted",
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
    /// Next step of a finished preview. A preview whose units all failed has
    /// nothing to apply, so it must never be presented as applicable.
    /// </summary>
    internal static string PreviewReadyHint(JobRecord job)
    {
        var preview = Preview(job);
        var failed = preview?.FailedCount ?? 0;
        var willChange = preview?.WillChangeCount ?? 0;
        if (failed > 0 && willChange == 0)
        {
            return "nothing to apply: all " + failed +
                   " unit(s) failed the preview. Fix the causes listed in failureCauses and run " +
                   "start_job with dryRun=true again";
        }

        var hint = "apply: call start_job with dryRun=false and previewDigest=" +
                   (preview?.Digest ?? "<digest>") +
                   ((preview?.Count ?? 0) > JobOptions.LargeJobThreshold
                       ? " plus allowLargeJobs=true (job above " + JobOptions.LargeJobThreshold + " units)"
                       : string.Empty);
        if (failed > 0)
        {
            hint += ". Only the " + willChange + " unit(s) that passed the preview are applied; the " +
                    failed + " failed unit(s) are reported as failed, not applied (see failureCauses)";
        }

        return hint;
    }

    /// <summary>
    /// Failed/mismatched units of a job, or null when there are none (no store
    /// round trip for a clean job).
    /// </summary>
    internal static async Task<IReadOnlyList<JobItem>?> LoadFailedItemsAsync(
        IJobStore store,
        JobRecord job,
        CancellationToken ct)
    {
        var totals = Totals(job);
        var failed = job.StartedAt is null
            ? Preview(job)?.FailedCount ?? totals.Failed
            : totals.Failed + totals.Mismatch + totals.UndoFailed;
        if (failed <= 0)
        {
            return null;
        }

        return await store.LoadItemsAsync(
            job.JobId,
            new[] { JobItemState.Failed, JobItemState.Mismatch, JobItemState.UndoFailed },
            0,
            1000000,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Skipped units of an applied job, or null when there are none (no store
    /// round trip for a clean run). Preview-time "already match" skips are not
    /// job items.
    /// </summary>
    internal static async Task<IReadOnlyList<JobItem>?> LoadSkippedItemsAsync(
        IJobStore store,
        JobRecord job,
        CancellationToken ct)
    {
        if (job.StartedAt is null || Totals(job).Skipped <= 0)
        {
            return null;
        }

        return await store.LoadItemsAsync(
            job.JobId,
            new[] { JobItemState.Skipped },
            0,
            1000000,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Skips grouped by reason: the stable code when there is one, the clipped
    /// detail for legacy text-only skips, already_matches when the unit needed
    /// no change at all.
    /// </summary>
    internal static object? SkippedReasons(IReadOnlyList<JobItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            return null;
        }

        return items
            .GroupBy(item =>
                    !string.IsNullOrWhiteSpace(item.ErrorCode)
                        ? item.ErrorCode!.Trim()
                        : !string.IsNullOrWhiteSpace(item.ErrorDetail)
                            ? Clip(item.ErrorDetail) ?? "skipped"
                            : "already_matches",
                StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => new
            {
                reason = group.Key,
                count = group.Count(),
                exampleUnit = group.First().Label
            })
            .ToArray();
    }

    /// <summary>One-line skip summary for the status message: "reason ×n, reason ×n".</summary>
    internal static string SkippedReasonsLine(IReadOnlyList<JobItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            ", ",
            items
                .GroupBy(item =>
                        !string.IsNullOrWhiteSpace(item.ErrorCode)
                            ? item.ErrorCode!.Trim()
                            : !string.IsNullOrWhiteSpace(item.ErrorDetail)
                                ? Clip(item.ErrorDetail) ?? "skipped"
                                : "already_matches",
                    StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key + " ×" + group.Count()));
    }

    /// <summary>Failures grouped by the code of the cause, biggest group first.</summary>
    private const int MaxFailedUnits = 5;

    internal static object? FailureCauses(IReadOnlyList<JobItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            return null;
        }

        return items
            .GroupBy(item => item.ErrorCode ?? JobErrorCodes.ItemFailed, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.Seq).ToList();
                return new
                {
                    errorCode = group.Key,
                    count = ordered.Count,
                    exampleUnit = ordered[0].Label,
                    exampleDetail = Clip(ordered.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ErrorDetail))?.ErrorDetail)
                };
            })
            .ToArray();
    }

    /// <summary>Compact per-unit failure list (label, code, reason) for the status payload.</summary>
    internal static object? FailedUnits(IReadOnlyList<JobItem>? items, int max)
    {
        if (items is null || items.Count == 0)
        {
            return null;
        }

        return items
            .OrderBy(item => item.Seq)
            .Take(Math.Max(0, max))
            .Select(item => new
            {
                seq = item.Seq,
                label = item.Label,
                status = item.State.ToString(),
                errorCode = item.ErrorCode,
                errorDetail = Clip(item.ErrorDetail)
            })
            .ToArray();
    }

    /// <summary>One-line cause summary for the status message: "code ×n, code ×n".</summary>
    internal static string CausesLine(IReadOnlyList<JobItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            return string.Empty;
        }

        return " Causes: " + string.Join(
            ", ",
            items
                .GroupBy(item => item.ErrorCode ?? JobErrorCodes.ItemFailed, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key + " ×" + group.Count())) + ".";
    }

    internal static string? Clip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        const int max = 400;
        return text!.Length <= max ? text : text.Substring(0, max) + "…";
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

        if (args.ValueKind != JsonValueKind.Object)
            return false;

        JsonElement value;
        if (!args.TryGetProperty("jobId", out value))
        {
            var found = false;
            foreach (var property in args.EnumerateObject())
            {
                if (string.Equals(property.Name, "jobId", StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        if (value.ValueKind == JsonValueKind.Null)
            return false;

        if (value.ValueKind != JsonValueKind.String)
        {
            // A numeric or object jobId used to be treated as "absent", and the
            // tool silently fell back to the most recent job — a rollback could
            // hit a different job than the one the user named.
            error = ToolResult.Fail(
                "invalid_arguments",
                "jobId must be a GUID string, got " + value.ValueKind +
                ". [Action: pass the jobId returned by start_job, or omit it to use the most recent job]");
            return false;
        }

        var text = value.GetString();
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
