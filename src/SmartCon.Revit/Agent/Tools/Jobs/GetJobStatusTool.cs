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
    private const int MaxWaitSeconds = 25;

    private readonly IJobStore _store;
    private readonly IJobScheduler _scheduler;

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
            "(max 25). Omit jobId for the most recent job of the active document. " +
            "includeSample=true adds the 5-item preview sample (default false).",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = true,
        SupportsBulk = true,
        Category = "jobs",
        Tags = new[] { "job", "status", "progress", "poll", "wait", "history" },
        TimeoutMs = 40000,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""jobId"":{""type"":""string""},""waitSeconds"":{""type"":""integer"",""minimum"":0,""maximum"":25},""includeSample"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}"
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
        return ToolResult.Ok(
            JobToolPayloads.Serialize(payload),
            job.State + " — " + summary + "." + JobToolPayloads.CausesLine(failedItems) +
            " " + JobToolPayloads.NextHint(job));
    }
}