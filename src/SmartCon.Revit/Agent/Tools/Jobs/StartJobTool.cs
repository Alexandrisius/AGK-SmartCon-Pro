using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Jobs;

namespace SmartCon.Revit.Agent.Tools.Jobs;

/// <summary>
/// Starts a bulk job (preview or apply) or resumes an interrupted one. The agent
/// calls this once; the job engine owns the loop (J-01, J-10).
/// </summary>
public sealed class StartJobTool : IAgentTool
{
    private readonly IJobScheduler _scheduler;
    private readonly IJobStore _store;
    private readonly IRevitContext _revitContext;

    public StartJobTool(IJobScheduler scheduler, IJobStore store, IRevitContext revitContext)
    {
        _scheduler = scheduler;
        _store = store;
        _revitContext = revitContext;
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "start_job",
        Description =
            "Starts a bulk job for hundreds of elements or dozens of families. " +
            "dryRun=true (default) previews only and returns previewDigest; " +
            "then call again with dryRun=false and that previewDigest to apply. " +
            "Actions: rename_views {select, rename}, set_parameters {select, parameter, value}, " +
            "map_family_parameters {select, mappings}. One call replaces hundreds of per-element calls.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        Category = "jobs",
        Tags = new[] { "job", "batch", "bulk", "mass", "preview", "apply", "resume" },
        InputSchemaJson = @"{""type"":""object"",""properties"":{""action"":{""type"":""string"",""enum"":[""rename_views"",""set_parameters"",""map_family_parameters""]},""spec"":{""type"":""object""},""dryRun"":{""type"":""boolean"",""default"":true},""previewDigest"":{""type"":""string""},""resumeJobId"":{""type"":""string""},""goal"":{""type"":""string""},""savePolicy"":{""type"":""string"",""enum"":[""after_each_batch"",""every_n_minutes"",""never""]},""batchSize"":{""type"":""integer"",""minimum"":1,""maximum"":500},""onError"":{""type"":""string"",""enum"":[""continue"",""abort""]},""rollbackOnError"":{""type"":""boolean"",""default"":false},""maxItems"":{""type"":""integer"",""minimum"":1,""maximum"":10000}},""required"":[""action""],""additionalProperties"":false}"
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var dryRun = AgentJson.GetBool(args, "dryRun", true);
        var action = AgentJson.GetString(args, "action") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(action))
        {
            return ToolResult.Fail("invalid_arguments", "action is required");
        }

        Guid? resumeId = null;
        var resumeText = AgentJson.GetString(args, "resumeJobId");
        if (!string.IsNullOrWhiteSpace(resumeText))
        {
            if (!Guid.TryParse(resumeText, out var parsed))
            {
                return ToolResult.Fail(
                    "invalid_arguments",
                    "resumeJobId must be a job GUID returned by start_job");
            }

            resumeId = parsed;
        }

        var digest = AgentJson.GetString(args, "previewDigest");
        var mode = resumeId is not null
            ? JobMode.Resume
            : dryRun
                ? JobMode.Preview
                : JobMode.Apply;

        if (mode == JobMode.Apply && string.IsNullOrWhiteSpace(digest))
        {
            return ToolResult.Fail(
                "job_preview_required",
                "Applying requires previewDigest from a completed preview. " +
                "[Action: call start_job with dryRun=true first, inspect the preview, " +
                "then call again with dryRun=false and that previewDigest]");
        }

        var specElement = args.TryGetProperty("spec", out var specValue)
            ? specValue
            : default;

        var specJson = specElement.ValueKind == JsonValueKind.Object
            ? specElement.GetRawText()
            : "{}";

        var spec = new JobSpec
        {
            DocumentId = context.DocumentId,
            Action = action,
            Mode = mode,
            Goal = AgentJson.GetString(args, "goal"),
            SpecJson = specJson,
            PreviewDigest = digest,
            ResumeJobId = resumeId,
            ConversationId = context.ConversationId == Guid.Empty ? null : context.ConversationId,
            Options = ReadOptions(args)
        };

        JobStartResult result;
        try
        {
            result = await context.Revit.RaiseAsync(
                _ => _scheduler.Start(spec, _revitContext.GetDocument()),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                "job_start_failed",
                ex.GetType().Name + ": " + ex.Message +
                " [Action: make sure the document is open and not read-only, then retry]");
        }

        if (!result.Success || result.Job is null)
        {
            var error = result.Error!;
            return ToolResult.Fail(error.Code, error.ToMessage());
        }

        var job = result.Job;
        var preview = JobToolPayloads.Preview(job);
        var payload = JobToolPayloads.Describe(job);

        var message = job.State switch
        {
            JobState.PreviewReady =>
                "Preview ready: " + preview?.Count + " unit(s), " + preview?.WillChangeCount +
                " will change, " + preview?.SkipCount + " already match. " +
                "Apply with dryRun=false and previewDigest.",
            JobState.PreviewRunning =>
                "Preview started: " + (preview?.Count ?? 0) + " unit(s). Call get_job_status to follow progress.",
            JobState.Running =>
                "Job started. Call get_job_status (waitSeconds=10) to follow progress.",
            JobState.Completed or JobState.CompletedWithFailures =>
                "Job finished: " + job.State + ". Call get_job_report for the per-item diff.",
            _ => "Job state: " + job.State
        };

        return ToolResult.Ok(
            JobToolPayloads.Serialize(payload),
            message,
            affectedCount: preview?.Count ?? 0);
    }

    private static JobOptions ReadOptions(JsonElement args)
    {
        var savePolicy = JobSavePolicy.AfterEachBatch;
        var policyText = AgentJson.GetString(args, "savePolicy");
        if (!string.IsNullOrWhiteSpace(policyText) &&
            Enum.TryParse<JobSavePolicy>(policyText, true, out var parsedPolicy))
        {
            savePolicy = parsedPolicy;
        }

        var onError = JobOnError.Continue;
        var onErrorText = AgentJson.GetString(args, "onError");
        if (!string.IsNullOrWhiteSpace(onErrorText) &&
            Enum.TryParse<JobOnError>(onErrorText, true, out var parsedOnError))
        {
            onError = parsedOnError;
        }

        return new JobOptions
        {
            BatchSize = AgentJson.GetInt(args, "batchSize", 50),
            SliceBudgetMs = 1500,
            OnError = onError,
            RollbackOnError = AgentJson.GetBool(args, "rollbackOnError", false),
            SavePolicy = savePolicy,
            MaxItems = AgentJson.GetInt(args, "maxItems", 1000)
        };
    }
}