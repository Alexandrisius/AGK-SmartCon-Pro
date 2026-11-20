using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Jobs;

namespace SmartCon.Revit.Agent.Tools.Jobs;

/// <summary>Resets failed units and resumes the job (B.2).</summary>
public sealed class RetryFailedItemsTool : IAgentTool
{
    private readonly IJobStore _store;
    private readonly IJobRecovery _recovery;
    private readonly IRevitContext _revitContext;

    public RetryFailedItemsTool(IJobStore store, IJobRecovery recovery, IRevitContext revitContext)
    {
        _store = store;
        _recovery = recovery;
        _revitContext = revitContext;
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "retry_failed_items",
        Description =
            "Re-evaluates and retries only the failed units of a job (fresh preview + apply, same " +
            "frozen units). Returns attempted/reset/stillFailed plus the real cause when a unit " +
            "still cannot be retried. Mismatched units are not retried: roll them back first. " +
            "Omit jobId for the most recent job of the active document.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = false,
        SupportsBulk = true,
        Category = "jobs",
        Tags = new[] { "job", "retry", "failed", "resume" },
        TimeoutMs = 180000,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""jobId"":{""type"":""string""}},""additionalProperties"":false}"
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var (jobId, resolveError) = await JobToolResolution
            .ResolveAsync(_store, args, context.DocumentId, ct)
            .ConfigureAwait(false);
        if (resolveError is not null)
        {
            return resolveError;
        }

        if (jobId is null)
        {
            return ToolResult.Fail(
                JobErrorCodes.JobNotFound,
                "No job found for this document. [Action: call get_job_status to list recent jobs]");
        }

        JobRetryResult result;
        try
        {
            result = await context.Revit.RaiseAsync(
                _ => _recovery.RetryFailed(_revitContext.GetDocument(), jobId.Value),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                JobErrorCodes.ItemFailed,
                ex.GetType().Name + ": " + ex.Message +
                " [Action: make sure the job's document is open, then retry]");
        }

        var payload = new
        {
            jobId = result.JobId.ToString("D"),
            attempted = result.Attempted,
            reset = result.Reset,
            totalFailed = result.TotalFailed,
            stillFailed = result.StillFailed,
            state = result.State.ToString(),
            totals = result.Totals,
            errorCode = result.ErrorCode,
            errorDetail = result.ErrorDetail
        };

        if (result.ErrorCode is not null)
        {
            return ToolResult.Fail(
                result.ErrorCode,
                (result.ErrorDetail ?? "Retry failed") +
                " [" + result.StillFailed + " of " + result.Attempted +
                " failed unit(s) still cannot be retried]",
                data: JobToolPayloads.Serialize(payload));
        }

        return ToolResult.Ok(
            JobToolPayloads.Serialize(payload),
            "Retried " + result.Attempted + " failed unit(s): " + result.Reset +
            " queued, " + result.StillFailed + " still failing; state " + result.State +
            ". Call get_job_status to follow progress.",
            affectedCount: result.Reset);
    }
}