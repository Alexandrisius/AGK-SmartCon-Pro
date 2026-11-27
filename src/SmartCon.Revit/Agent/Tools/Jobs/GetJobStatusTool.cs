using System.Globalization;
using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Jobs;

/// <summary>
/// Reads job progress from SQLite without touching the Revit API, so the
/// long-poll never freezes the UI and never blocks the job pump.
/// </summary>
public sealed class GetJobStatusTool : IAgentTool
{
    /// <summary>
    /// Long-poll ceiling sized for family-edit jobs: one heavy family edit can
    /// legitimately run for tens of minutes (live v18 — 44 minutes on one
    /// unit), and a model that can only wait 25 seconds falls into a tight
    /// poll loop (thousands of calls → provider rate limit → dead session).
    /// The wait reads SQLite off the Revit thread, so a long hold blocks
    /// nothing.
    /// </summary>
    private const int MaxWaitSeconds = 240;

    /// <summary>Identical instant polls before the answer starts pushing the
    /// long-poll instead of feeding the loop.</summary>
    private const int PollHintAfter = 3;

    private readonly IJobStore _store;
    private readonly IJobScheduler _scheduler;
    private readonly object _pollGate = new();
    private string? _lastPollKey;
    private int _instantPollStreak;

    public GetJobStatusTool(IJobStore store, IJobScheduler scheduler)
    {
        _store = store;
        _scheduler = scheduler;
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_job_status",
        Description =
            "Returns progress of a bulk job: state, counters, saved cursor, preview digest, " +
            "recent job history and the next step. waitSeconds long-polls without blocking Revit " +
            "(up to 240 s — the answer arrives when the job settles, a heavy family edit can run " +
            "for tens of minutes; do NOT poll in a loop, one long wait replaces dozens of instant " +
            "calls). Omit jobId for the most recent job of the active document. " +
            "includeSample=true adds the 5-item preview sample (default false).",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = true,
        SupportsBulk = true,
        Category = "jobs",
        Tags = new[] { "job", "status", "progress", "poll", "wait", "history" },
        TimeoutMs = 300000,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""jobId"":{""type"":""string""},""waitSeconds"":{""type"":""integer"",""minimum"":0,""maximum"":240,""description"":""Long-poll: hold the call until the job settles or the wait expires; 120-240 s replaces a poll loop""},""includeSample"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}"
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var waitSeconds = Math.Min(
            Math.Max(AgentJson.GetInt(args, "waitSeconds", 0), 0),
            MaxWaitSeconds);

        _scheduler.EnsurePump();

        JobRecord? job = null;
        var hasExplicitId = JobToolPayloads.TryReadJobId(args, out var explicitId, out var parseError);
        if (parseError is not null)
        {
            return parseError;
        }


        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (true)
        {
            job = hasExplicitId
                ? await _store.GetAsync(explicitId, ct).ConfigureAwait(false)
                : await _store.GetActiveAsync(context.DocumentId, ct).ConfigureAwait(false);

            if (job is null)
            {
                break;
            }

            if (!string.IsNullOrEmpty(job.Spec.DocumentId) &&
                !string.Equals(job.Spec.DocumentId, context.DocumentId, StringComparison.Ordinal) &&
                hasExplicitId)
            {
                return ToolResult.Fail(
                    JobErrorCodes.DocumentMismatch,
                    JobErrors.DocumentMismatch(job.Spec.DocumentId, context.DocumentId).ToMessage());
            }

            var settled = job.State is JobState.PreviewReady or JobState.Completed
                or JobState.CompletedWithFailures or JobState.CompletedUnverified
                or JobState.Failed or JobState.Cancelled
                or JobState.RolledBack or JobState.RollbackPartial or JobState.Interrupted;

            if (settled || DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        var recent = await _store.ListRecentAsync(context.DocumentId, 5, ct).ConfigureAwait(false);
        if (job is null)
        {
            var recentJobs = JobToolPayloads.History(recent);
            if (hasExplicitId)
            {
                return ToolResult.Fail(
                    JobErrorCodes.JobNotFound,
                    "Job '" + explicitId.ToString("D") + "' was not found. " +
                    "[Action: use a jobId from recentJobs or omit jobId for the latest job]",
                    data: JobToolPayloads.Serialize(new
                    {
                        state = "not_found",
                        recentJobs
                    }));
            }

            // "No active job" is a valid state, not an error: return the history so
            // the agent can pick a jobId without replaying stale tool output.
            return ToolResult.Ok(
                JobToolPayloads.Serialize(new
                {
                    state = "idle",
                    recentJobs
                }),
                recent.Count == 0
                    ? "No job has run in this document. [Action: call start_job to create one]"
                    : "No job is active. Recent jobs: " + string.Join(
                        ", ", recent.Select(item => item.JobId.ToString("D") + " (" + item.State + ")")));
        }

        var includeSample = AgentJson.GetBool(args, "includeSample", false);
        // Failure causes belong to every status: grouped by the code of the cause,
        // so the model learns why units failed without get_job_report. Skip
        // reasons ride along for the same reason (S.47, #532).
        var failedItems = await JobToolPayloads.LoadFailedItemsAsync(_store, job, ct).ConfigureAwait(false);
        var skippedItems = await JobToolPayloads.LoadSkippedItemsAsync(_store, job, ct).ConfigureAwait(false);
        var payload = JobToolPayloads.Describe(
            job,
            includeSample,
            JobToolPayloads.History(recent),
            failedItems,
            skippedItems);
        var totals = JobToolPayloads.Totals(job);
        var summary = totals.Done + " done, " + totals.Skipped + " skipped, " +
                      totals.Failed + " failed, " + totals.Mismatch + " mismatch";

        // ETA in the message: a long job must be distinguishable from a hung one
        // without waiting for a timeout (live agent run: 200+ units, no signal).
        var pace = JobToolPayloads.PaceOf(job, totals);
        var pending = totals.Pending + totals.Previewed;
        var etaText = pace is null || pending <= 0
            ? string.Empty
            : " Pace: " + pace.Value.ToString("0.##", CultureInfo.InvariantCulture) +
              " unit(s)/min, ~" + Math.Ceiling(pending / pace.Value) + " min left for " +
              pending + " unit(s).";

        // Poll-loop guard (live v18: ~2100 instant status calls → provider
        // rate limit → dead session): a streak of identical instant polls
        // gets a direct instruction to long-poll instead. The wait itself is
        // the fix — the guard only names it.
        var pollHint = string.Empty;
        lock (_pollGate)
        {
            var pollKey = job.JobId.ToString("D") + ":" + job.State + ":" + totals.Done;
            if (waitSeconds > 0)
            {
                _instantPollStreak = 0;
                _lastPollKey = null;
            }
            else if (pollKey == _lastPollKey)
            {
                _instantPollStreak++;
                if (_instantPollStreak >= PollHintAfter)
                {
                    pollHint = " This is instant status call #" + _instantPollStreak +
                               " in a row with an unchanged answer; polling again changes nothing. " +
                               "[Action: pass waitSeconds=240 — the call holds until the job settles " +
                               "(a heavy family edit can run for tens of minutes) — and stop looping]";
                }
            }
            else
            {
                _instantPollStreak = 0;
                _lastPollKey = pollKey;
            }
        }

        return ToolResult.Ok(
            JobToolPayloads.Serialize(payload),
            job.State + " — " + summary + "." + etaText + pollHint + JobToolPayloads.CausesLine(failedItems) +
            " " + JobToolPayloads.NextHint(job));
    }
}