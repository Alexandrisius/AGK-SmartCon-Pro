using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Jobs;

namespace SmartCon.Revit.Agent.Tools.Jobs;

/// <summary>Per-item rollback of an applied job (B.2).</summary>
public sealed class RollbackJobTool : IAgentTool
{
    private readonly IJobStore _store;
    private readonly IJobRecovery _recovery;
    private readonly IRevitContext _revitContext;

    public RollbackJobTool(IJobStore store, IJobRecovery recovery, IRevitContext revitContext)
    {
        _store = store;
        _recovery = recovery;
        _revitContext = revitContext;
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "rollback_job",
        Description =
            "Restores every applied unit of a job from its per-item undo data. dryRun=true (default) " +
            "reports what would be restored without changing the model. " +
            "Omit jobId for the most recent job of the active document.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        Category = "jobs",
        Tags = new[] { "job", "rollback", "undo", "restore" },
        TimeoutMs = 180000,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""jobId"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
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

        var dryRun = AgentJson.GetBool(args, "dryRun", true);

        JobRollbackResult result;
        try
        {
            result = await context.Revit.RaiseAsync(
                _ => _recovery.Rollback(_revitContext.GetDocument(), jobId.Value, dryRun),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                JobErrorCodes.RollbackFailed,
                ex.GetType().Name + ": " + ex.Message +
                " [Action: make sure the job's document is open; nothing was restored by this call]");
        }

        var payload = new
        {
            jobId = result.JobId.ToString("D"),
            dryRun = result.DryRun,
            state = result.State.ToString(),
            restored = result.Restored,
            restoreFailed = result.RestoreFailed,
            skipped = result.Skipped,
            errorCode = result.ErrorCode,
            errorDetail = result.ErrorDetail
        };

        if (result.ErrorCode is not null && result.Restored == 0 && result.RestoreFailed == 0)
        {
            return ToolResult.Fail(
                result.ErrorCode,
                result.ErrorDetail ?? "Rollback is unavailable",
                data: JobToolPayloads.Serialize(payload));
        }

        var message = dryRun
            ? "Rollback preview: " + result.Restored + " restorable, " + result.RestoreFailed +
              " would fail, " + result.Skipped + " already restored. Call again with dryRun=false to apply."
            : "Rollback: " + result.Restored + " restored, " + result.RestoreFailed + " failed, " +
              result.Skipped + " skipped (state " + result.State + ")";

        return ToolResult.Ok(
            JobToolPayloads.Serialize(payload),
            message,
            affectedCount: result.Restored);
    }
}

/// <summary>Resolves a job id from arguments or falls back to the most recent job.</summary>
internal static class JobToolResolution
{
    internal static async Task<(Guid? JobId, ToolResult? Error)> ResolveAsync(
        IJobStore store,
        JsonElement args,
        string documentId,
        CancellationToken ct)
    {
        if (JobToolPayloads.TryReadJobId(args, out var explicitId, out var parseError))
        {
            return (explicitId, null);
        }

        if (parseError is not null)
        {
            return (null, parseError);
        }

        var active = await store.GetActiveAsync(documentId, ct).ConfigureAwait(false);
        if (active is not null)
        {
            return (active.JobId, null);
        }

        var recent = await store.ListRecentAsync(documentId, 1, ct).ConfigureAwait(false);
        return (recent.Count == 0 ? null : recent[0].JobId, null);
    }
}