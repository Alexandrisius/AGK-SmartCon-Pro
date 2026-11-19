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
            "Returns progress of a bulk job: state, counters, saved cursor, preview digest and " +
            "the next step. waitSeconds long-polls without blocking Revit (max 25). " +
            "Omit jobId for the most recent job of the active document.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = true,
        SupportsBulk = true,
        Category = "jobs",
        Tags = new[] { "job", "status", "progress", "poll", "wait" },
        TimeoutMs = 40000,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""jobId"":{""type"":""string""},""waitSeconds"":{""type"":""integer"",""minimum"":0,""maximum"":25}},""additionalProperties"":false}"
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
        var hasExplicitId = JobToolPayloads.TryParseJobId(args, out var explicitId);

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
                or JobState.CompletedWithFailures or JobState.Failed or JobState.Cancelled
                or JobState.RolledBack or JobState.RollbackPartial or JobState.Interrupted;

            if (settled || DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        if (job is null)
        {
            var recent = await _store.ListRecentAsync(context.DocumentId, 5, ct).ConfigureAwait(false);
            return ToolResult.Fail(
                JobErrorCodes.JobNotFound,
                "No job is active for this document. [Action: call start_job to create one; " +
                "recent jobs: " + string.Join(", ", recent.Select(item => item.JobId.ToString("D"))) + "]",
                data: JobToolPayloads.Serialize(new
                {
                    state = hasExplicitId ? "not_found" : "idle",
                    recentJobIds = recent.Select(item => item.JobId.ToString("D")).ToArray()
                }));
        }

        var payload = JobToolPayloads.Describe(job);
        return ToolResult.Ok(JobToolPayloads.Serialize(payload), job.State + " — " + JobToolPayloads.NextHint(job));
    }
}