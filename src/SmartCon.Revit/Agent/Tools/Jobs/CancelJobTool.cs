using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Jobs;

/// <summary>Requests cancellation; the runner stops between units (J-02).</summary>
public sealed class CancelJobTool : IAgentTool
{
    private readonly IJobScheduler _scheduler;
    private readonly IJobStore _store;

    public CancelJobTool(IJobScheduler scheduler, IJobStore store)
    {
        _scheduler = scheduler;
        _store = store;
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "cancel_job",
        Description =
            "Cancels a running bulk job. The runner stops between units, so applied units stay " +
            "applied unless the job was started with rollbackOnError=true. " +
            "Omit jobId for the most recent job of the active document.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = false,
        SupportsBulk = true,
        Category = "jobs",
        Tags = new[] { "job", "cancel", "stop", "abort" },
        InputSchemaJson = @"{""type"":""object"",""properties"":{""jobId"":{""type"":""string""}},""additionalProperties"":false}"
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var hasExplicitId = JobToolPayloads.TryReadJobId(args, out var explicitId, out var parseError);
        if (parseError is not null)
        {
            return parseError;
        }

        Guid? jobId = hasExplicitId ? explicitId : null;
        if (jobId is null)
        {
            var active = await _store.GetActiveAsync(context.DocumentId, ct).ConfigureAwait(false);
            jobId = active?.JobId;
        }

        if (jobId is null)
        {
            return ToolResult.Fail(
                JobErrorCodes.JobNotFound,
                "No job is active for this document. [Action: nothing to cancel; call get_job_status to list jobs]");
        }

        var cancelled = _scheduler.Cancel(jobId.Value, context.DocumentId);
        var job = await _store.GetAsync(jobId.Value, ct).ConfigureAwait(false);

        if (!cancelled)
        {
            return ToolResult.Fail(
                JobErrorCodes.InvalidState,
                "The job cannot be cancelled in state '" + (job?.State.ToString() ?? "unknown") + "'. " +
                "[Action: call get_job_status; only running, previewing or interrupted jobs can be cancelled]",
                data: job is null ? null : JobToolPayloads.Serialize(JobToolPayloads.Describe(job)));
        }

        var payload = job is null ? null : JobToolPayloads.Describe(job);
        return ToolResult.Ok(
            payload is null ? null : JobToolPayloads.Serialize(payload),
            "Cancellation requested" + (job is null ? string.Empty : " (state: " + job.State + ")") +
            ". Call get_job_status to confirm the final state.");
    }
}