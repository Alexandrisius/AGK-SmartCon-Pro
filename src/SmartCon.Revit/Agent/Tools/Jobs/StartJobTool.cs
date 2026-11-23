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

    private const string JobInputSchema = """
{
  "type": "object",
  "properties": {
    "action": { "type": "string", "enum": ["rename_views", "set_parameters", "map_family_parameters", "replace_family_parameter"], "description": "rename_views and set_parameters handle up to 5000 units; map_family_parameters and replace_family_parameter are hard-capped at 200 family units per job." },
    "spec": {
      "type": "object",
      "properties": {
        "select": {
          "type": "object",
          "properties": {
            "viewIds": { "type": "array", "items": { "type": "integer" } },
            "elementIds": { "type": "array", "items": { "type": "integer" } },
            "nameContains": { "type": "string" },
            "nameStartsWith": { "type": "string" },
            "nameEndsWith": { "type": "string" },
            "viewTypes": { "type": "array", "items": { "type": "string" } },
            "types": { "type": "array", "items": { "type": "string" } },
            "viewType": { "type": "string" },
            "type": { "type": "string" },
            "levelIds": { "type": "array", "items": { "type": "integer" } },
            "folders": { "type": "array", "items": { "type": "string" } },
            "folder": { "type": "string" },
            "templateName": { "type": "string" },
            "template": { "type": "string" },
            "templateId": { "type": "integer" },
            "hasTemplate": { "type": "boolean" },
            "isTemplate": { "type": "boolean" },
            "includeTemplates": { "type": "boolean" },
            "excludeTemplates": { "type": "boolean" },
            "excludeSheets": { "type": "boolean" },
            "sheetNumber": { "type": "string" },
            "onSheet": { "type": "boolean" },
            "familyIds": { "type": "array", "items": { "type": "integer" } },
            "familyUniqueIds": { "type": "array", "items": { "type": "string" } },
            "familyNames": { "type": "array", "items": { "type": "string" }, "maxItems": 200, "description": "Family names to include; stable across reloads (preferred over familyIds which change on every reload)" },
            "categoryIds": { "type": "array", "items": { "type": "integer" } },
            "categoryNames": { "type": "array", "items": { "type": "string" } },
            "typeIds": { "type": "array", "items": { "type": "integer" } },
            "familyName": { "type": "string" },
            "typeName": { "type": "string" },
            "conditions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "parameter": { "type": "string" },
                  "op": { "type": "string", "enum": ["eq", "neq", "gt", "gte", "lt", "lte", "contains", "startsWith", "endsWith", "in", "notIn", "exists", "notExists"] },
                  "value": {},
                  "unit": { "type": "string" }
                },
                "required": ["parameter", "op"]
              }
            },
            "logic": { "type": "string", "enum": ["and", "or"] },
            "includeTypeParameters": { "type": "boolean" },
            "systemName": { "type": "string", "description": "set_parameters only: exact MEP system name (see list_mep_systems); the selection becomes the members of that system — terminal elements + base equipment, plus pipes and fittings for piping systems / ducts and fittings for mechanical systems" },
            "systemDomain": { "type": "string", "enum": ["piping", "mechanical", "electrical"], "description": "set_parameters only: optional domain filter for systemName" },
            "includeSubComponents": { "type": "boolean", "default": false, "description": "set_parameters only: recursively add nested sub-components (shared nested family instances like flanges) of every selected element, up to depth 3, so they receive the parameter too" },
            "limit": { "type": "integer", "minimum": 1 }
          }
        },
        "rename": {
          "type": "object",
          "properties": {
            "find": { "type": "string" },
            "replace": { "type": "string" },
            "prefix": { "type": "string" },
            "suffix": { "type": "string" },
            "setName": { "type": "string", "description": "Supports {n}, {name}, {index}." },
            "numbering": {
              "type": "object",
              "properties": {
                "start": { "type": "integer" },
                "step": { "type": "integer" },
                "padding": { "type": "integer", "minimum": 0 },
                "format": { "type": "string", "description": "Default {base}_{n}." }
              }
            }
          }
        },
        "mappings": {
          "type": "array",
          "description": "One source->target per mapping. map_family_parameters processes at most 200 family units per job; split larger migrations into several jobs.",
          "items": {
            "type": "object",
            "properties": {
              "source": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "scope": { "type": "string", "enum": ["instance", "type"] },
                  "guid": { "type": "string" }
                }
              },
              "target": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "scope": { "type": "string", "enum": ["instance", "type"] },
                  "guid": { "type": "string" }
                }
              },
              "mode": { "type": "string", "enum": ["values", "formula"] },
              "typeNames": { "type": "array", "items": { "type": "string" } },
              "createTarget": { "type": "boolean", "default": false, "description": "Create the missing target parameter from a shared definition (source spec/group); a definition written to the shared parameter file is not reverted by job undo" }
            }
          }
        },
        "replacements": {
          "type": "array",
          "description": "replace_family_parameter only: source->target shared definition pairs; at most 50 per job. The target definition must already exist in the shared parameter file; the source keeps its scope, values, formulas and labels.",
          "items": {
            "type": "object",
            "properties": {
              "source": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "guid": { "type": "string" }
                }
              },
              "target": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "guid": { "type": "string" }
                }
              }
            }
          }
        },
        "parameter": { "type": "string" },
        "guid": { "type": "string" },
        "builtInParameter": { "type": "string" },
        "scope": { "type": "string", "enum": ["instance", "type"] },
        "value": { "type": "string" },
        "unit": { "type": "string" }
      }
    },
    "mode": { "type": "string", "enum": ["preview", "apply", "resume"], "description": "Optional alias for dryRun/resumeJobId; dryRun and resumeJobId remain the primary contract." },
    "dryRun": { "type": "boolean", "default": true },
    "previewDigest": { "type": "string" },
    "resumeJobId": { "type": "string" },
    "goal": { "type": "string" },
    "savePolicy": { "type": "string", "enum": ["after_each_batch", "every_n_minutes", "never"], "default": "never" },
    "batchSize": { "type": "integer", "minimum": 1, "maximum": 500, "default": 50 },
    "onError": { "type": "string", "enum": ["continue", "abort"] },
    "rollbackOnError": { "type": "boolean", "default": false },
    "maxItems": { "type": "integer", "minimum": 1, "maximum": 10000, "default": 1000, "description": "Upper bound for selected units (<=10000). Per-action caps still apply: rename_views/set_parameters 5000, map_family_parameters/replace_family_parameter 200. allowLargeJobs only acknowledges apply/resume above 200 units; it does not raise an action cap." },
    "allowLargeJobs": { "type": "boolean", "default": false, "description": "Required to apply/resume more than 200 units; a preview cannot grant it." },
    "includeSample": { "type": "boolean", "description": "Default true for preview, false for apply/resume; set false to omit the preview sample." }
  },
  "required": ["action"],
  "additionalProperties": false
}
""";

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "start_job",
        Description =
            "Starts a bulk job: select units, preview, apply. Per-action caps: rename_views and " +
            "set_parameters handle up to 5000 units; map_family_parameters and " +
            "replace_family_parameter handle up to 200 family units per job (a hard cap — " +
            "allowLargeJobs does not raise it). maxItems is " +
            "an upper bound (<=10000); the smaller per-action cap still applies. " +
            "dryRun=true (default) previews only and returns previewDigest; " +
            "then call again with dryRun=false and that previewDigest to apply. " +
            "resumeJobId alone resumes an interrupted/failed job. Actions: rename_views, " +
            "set_parameters, map_family_parameters, replace_family_parameter. The typed spec " +
            "schema lists every selector, rename, mapping and replacement key; unknown spec " +
            "keys are rejected with the allowed-key list. " +
            "One call replaces hundreds of per-element calls.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        Category = "jobs",
        Tags = new[] { "job", "batch", "bulk", "mass", "preview", "apply", "resume" },
        InputSchemaJson = JobInputSchema
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
        var includeSampleArg = Has(args, "includeSample")
            ? AgentJson.GetBool(args, "includeSample", true)
            : (bool?)null;

        JobMode? explicitMode = null;
        var modeText = AgentJson.GetString(args, "mode");
        if (!string.IsNullOrWhiteSpace(modeText))
        {
            switch (modeText!.Trim().ToLowerInvariant())
            {
                case "preview":
                    explicitMode = JobMode.Preview;
                    break;
                case "apply":
                    explicitMode = JobMode.Apply;
                    break;
                case "resume":
                    explicitMode = JobMode.Resume;
                    break;
                default:
                    return ToolResult.Fail(
                        "invalid_arguments",
                        "mode '" + modeText + "' is not supported. [Action: pass preview, apply or resume, " +
                        "or omit mode and use dryRun/resumeJobId]");
            }
        }

        if (resumeId is not null && explicitMode is JobMode.Preview or JobMode.Apply)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "resumeJobId cannot be combined with mode=" + explicitMode +
                ". [Action: omit mode when resuming, or omit resumeJobId for a new run]");
        }

        if (explicitMode == JobMode.Resume && resumeId is null)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "mode=resume requires resumeJobId. [Action: pass resumeJobId from get_job_status]");
        }

        var mode = resumeId is not null
            ? JobMode.Resume
            : explicitMode ?? (dryRun ? JobMode.Preview : JobMode.Apply);
        var includeSample = includeSampleArg ?? mode == JobMode.Preview;

        // mode/resumeJobId are aliases that bypass the dryRun flag. The executor
        // now asks for confirmation for them, and this guard makes an unapproved
        // mass write impossible even if another caller skips that gate.
        if (mode is JobMode.Apply or JobMode.Resume && !context.WriteApproved)
        {
            return ToolResult.Fail(
                "confirmation_required",
                "start_job with mode=" + mode.ToString().ToLowerInvariant() +
                " applies changes but this call was not confirmed by the user. " +
                "[Action: do not retry silently; the write needs an explicit confirmation]");
        }

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

        var (options, optionsOverride, optionsError) = ReadOptions(args);
        if (optionsError is not null)
        {
            return optionsError;
        }

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
            Options = options,
            OptionsOverride = optionsOverride
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

        // A short job (a family preview of a few units) normally settles within a
        // second or two. The pump is raised at once and the call waits a bounded
        // time for the settled state, so the answer carries the finished preview
        // instead of costing two more rounds (get_job_status, get_job_report).
        if (JobStateMachine.NeedsPump(job.State))
        {
            _scheduler.EnsurePump();
            job = await WaitForSettledAsync(job.JobId, ct).ConfigureAwait(false) ?? job;
        }

        var preview = JobToolPayloads.Preview(job);
        var failedItems = await JobToolPayloads.LoadFailedItemsAsync(_store, job, ct).ConfigureAwait(false);
        var payload = JobToolPayloads.Describe(job, includeSample: includeSample, failedItems: failedItems);
        var failed = preview?.FailedCount ?? 0;
        var totals = JobToolPayloads.Totals(job);
        var causes = JobToolPayloads.CausesLine(failedItems);

        var message = job.State switch
        {
            JobState.PreviewReady =>
                "Preview ready: " + preview?.Count + " unit(s), " + preview?.WillChangeCount +
                " will change, " + preview?.SkipCount + " already match, " + failed + " failed." +
                (failed == 0
                    ? " Apply with dryRun=false and previewDigest."
                    : " " + JobToolPayloads.PreviewReadyHint(job)),
            JobState.PreviewRunning =>
                "Preview still running after " + (SettleWaitMs / 1000) +
                " s. Call get_job_status (waitSeconds=10) to follow progress.",
            JobState.Running =>
                "Job still running: " + totals.Done + " done, " + totals.Failed + " failed, " +
                totals.Mismatch + " mismatch. Call get_job_status (waitSeconds=10) until the state " +
                "is terminal and mustVerify=false; do not report it as finished before that.",
            JobState.Completed or JobState.CompletedWithFailures or JobState.CompletedUnverified =>
                "Job finished: " + job.State + " — " + totals.Done + " done, " + totals.Skipped +
                " skipped, " + totals.Failed + " failed, " + totals.Mismatch + " mismatch." +
                (failedItems is { Count: > 0 }
                    ? causes + " Fix the causes, then retry_failed_items or call get_job_report."
                    : " Call get_job_report for the per-item diff."),
            JobState.Failed =>
                "Job failed: " + job.ErrorCode + ": " + job.ErrorDetail,
            _ => "Job state: " + job.State
        };

        var warnings = failedItems is { Count: > 0 }
            ? failedItems
                .Take(5)
                .Where(item => !string.IsNullOrWhiteSpace(item.ErrorDetail))
                .Select(item =>
                    (item.Label ?? item.UnitKey ?? "unit") + ": " +
                    (item.ErrorCode ?? JobErrorCodes.ItemFailed) + " — " + item.ErrorDetail)
                .ToArray()
            : null;

        return ToolResult.Ok(
            JobToolPayloads.Serialize(payload),
            message,
            affectedCount: preview?.Count ?? 0,
            warnings: warnings);
    }

    /// <summary>Upper bound for waiting until the started job settles inside start_job.</summary>
    private const int SettleWaitMs = 8000;

    private async Task<JobRecord?> WaitForSettledAsync(Guid jobId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(SettleWaitMs);
        while (true)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            var job = await _store.GetAsync(jobId, ct).ConfigureAwait(false);
            if (job is null || !JobStateMachine.NeedsPump(job.State) || DateTime.UtcNow >= deadline)
            {
                return job;
            }
        }
    }

    private static (JobOptions Options, JobOptionsOverride? Overrides, ToolResult? Error) ReadOptions(
        JsonElement args)
    {
        var hasSavePolicy = Has(args, "savePolicy");
        // Never by default: the job engine must not rewrite the user's .rvt on
        // disk (owner decision 2026-11-21); an explicit savePolicy opts in.
        var savePolicy = JobSavePolicy.Never;
        var policyText = AgentJson.GetString(args, "savePolicy");
        if (!string.IsNullOrWhiteSpace(policyText) &&
            !TryParseSavePolicy(policyText!, out savePolicy))
        {
            return (new JobOptions(), null, ToolResult.Fail(
                "invalid_arguments",
                "savePolicy '" + policyText + "' is not supported. " +
                "[Action: pass after_each_batch, every_n_minutes or never]"));
        }

        var hasOnError = Has(args, "onError");
        var onError = JobOnError.Continue;
        var onErrorText = AgentJson.GetString(args, "onError");
        if (!string.IsNullOrWhiteSpace(onErrorText) &&
            !Enum.TryParse(onErrorText, true, out onError))
        {
            return (new JobOptions(), null, ToolResult.Fail(
                "invalid_arguments",
                "onError '" + onErrorText + "' is not supported. [Action: pass continue or abort]"));
        }

        var hasBatchSize = Has(args, "batchSize");
        var batchSize = AgentJson.GetInt(args, "batchSize", 50);
        if (batchSize is <= 0 or > 500)
        {
            return (new JobOptions(), null, ToolResult.Fail(
                "invalid_arguments",
                "batchSize must be 1..500. [Action: pass a smaller batch size]"));
        }

        var hasMaxItems = Has(args, "maxItems");
        var maxItems = AgentJson.GetInt(args, "maxItems", 1000);
        if (maxItems is <= 0 or > 10000)
        {
            return (new JobOptions(), null, ToolResult.Fail(
                "invalid_arguments",
                "maxItems must be 1..10000. [Action: pass a smaller limit]"));
        }

        var hasRollback = Has(args, "rollbackOnError");
        var rollback = AgentJson.GetBool(args, "rollbackOnError", false);
        var hasAllowLarge = Has(args, "allowLargeJobs");
        var allowLarge = AgentJson.GetBool(args, "allowLargeJobs", false);

        var options = new JobOptions
        {
            BatchSize = batchSize,
            SliceBudgetMs = 1500,
            OnError = onError,
            RollbackOnError = rollback,
            SavePolicy = savePolicy,
            MaxItems = maxItems,
            AllowLargeJob = allowLarge
        };

        // Only explicitly supplied keys override the preview's stored policies.
        var overrides = new JobOptionsOverride
        {
            OnError = hasOnError ? onError : null,
            RollbackOnError = hasRollback ? rollback : null,
            SavePolicy = hasSavePolicy ? savePolicy : null,
            BatchSize = hasBatchSize ? batchSize : null,
            MaxItems = hasMaxItems ? maxItems : null,
            AllowLargeJob = hasAllowLarge ? allowLarge : null
        };

        return (options, overrides, null);
    }

    private static bool Has(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (args.TryGetProperty(name, out _))
        {
            return true;
        }

        foreach (var property in args.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseSavePolicy(string text, out JobSavePolicy policy)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "after_each_batch":
                policy = JobSavePolicy.AfterEachBatch;
                return true;
            case "every_n_minutes":
                policy = JobSavePolicy.EveryNMinutes;
                return true;
            case "never":
                policy = JobSavePolicy.Never;
                return true;
            default:
                return Enum.TryParse(text, true, out policy);
        }
    }
}