using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Jobs;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>Acceptance feedback round: typed selectors, policies, guards and retry.</summary>
public sealed partial class AgentProjectJobTests
{
    [Test]
    public async Task Job_RenameSelect_ByTypeAndLevel_SelectsPlans()
    {
        var specJson =
            "{\"select\":{\"type\":\"FloorPlan\",\"levelIds\":[" + _levelId +
            "]},\"rename\":{\"prefix\":\"SC_Type_\"}}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "rename_views",
                Mode = JobMode.Preview,
                SpecJson = specJson,
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        var previewRecord = await Store.GetAsync(preview.Job!.JobId, CancellationToken.None);
        var data = JobJson.Deserialize<JobPreview>(previewRecord!.PreviewJson)!;
        await Assert.That(data.Count).IsEqualTo(ViewCount);
        await Assert.That(data.WillChangeCount).IsEqualTo(ViewCount);

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "rename_views",
                Mode = JobMode.Apply,
                SpecJson = specJson,
                PreviewDigest = data.Digest,
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);
        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        await Assert.That(result.State).IsEqualTo(JobState.Completed);
        await Assert.That(CollectViewNames().Count(name => name.StartsWith("SC_Type_"))).IsEqualTo(ViewCount);
    }

    [Test]
    public async Task Job_RenameSelect_UnknownKey_IsRejectedWithAllowedKeys()
    {
        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "rename_views",
                Mode = JobMode.Preview,
                SpecJson =
                    "{\"select\":{\"levelId\":" + _levelId +
                    ",\"levelIds\":[" + _levelId + "]},\"rename\":{\"prefix\":\"X_\"}}",
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);

        await Assert.That(preview.Success).IsFalse();
        await Assert.That(preview.Error!.Code).IsEqualTo(JobErrorCodes.InvalidSpec);
        await Assert.That(preview.Error.Message).Contains("select.levelId");
        await Assert.That(preview.Error.Message).Contains("Allowed keys");
        await Assert.That(preview.Error.Message).Contains("levelIds");
    }

    [Test]
    public async Task Job_SetParameters_ConditionFiltersElements()
    {
        var walls = new List<long>();
        _transactions!.RunInTransaction(Doc, "Seed condition walls", doc =>
        {
            for (var index = 0; index < 3; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, ElementIdCompat.Create(_levelId), 20 + index);
                if (wall is null)
                {
                    return;
                }

                wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(
                    index < 2 ? "keep" : "drop");
                walls.Add(wall.Id.GetValue());
            }
        });

        if (walls.Count != 3)
        {
            Skip.Test("The project template has no wall type to seed walls");
        }

        var specJson =
            "{\"select\":{\"categoryNames\":[\"OST_Walls\"],\"conditions\":[" +
            "{\"parameter\":\"ALL_MODEL_INSTANCE_COMMENTS\",\"op\":\"eq\",\"value\":\"keep\"}]}," +
            "\"builtInParameter\":\"ALL_MODEL_INSTANCE_COMMENTS\",\"value\":\"done\",\"scope\":\"instance\"}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Preview,
                SpecJson = specJson,
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var previewRecord = await Store.GetAsync(preview.Job!.JobId, CancellationToken.None);
        var data = JobJson.Deserialize<JobPreview>(previewRecord!.PreviewJson)!;
        await Assert.That(data.WillChangeCount).IsEqualTo(2);

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Apply,
                SpecJson = specJson,
                PreviewDigest = data.Digest,
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));

        var values = walls
            .Select(id => Doc.GetElement(ElementIdCompat.Create(id))
                ?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())
            .ToList();
        await Assert.That(values.Count(value => value == "done")).IsEqualTo(2);
        await Assert.That(values.Count(value => value == "drop")).IsEqualTo(1);
    }

    [Test]
    public async Task Job_RenameSetNameWithNumbering_AppliesPaddingOnce()
    {
        var views = new List<long>();
        foreach (var view in new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>())
        {
            if (view.Name is "SC_Job_View_1" or "SC_Job_View_2" or "SC_Job_View_3")
            {
                views.Add(view.Id.GetValue());
            }
        }

        await Assert.That(views.Count).IsEqualTo(3);

        var specJson =
            "{\"select\":{\"viewIds\":[" + string.Join(",", views) + "]}," +
            "\"rename\":{\"setName\":\"SC_Plan_{n}\",\"numbering\":{\"start\":1,\"step\":1,\"padding\":2}}}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "rename_views",
                Mode = JobMode.Preview,
                SpecJson = specJson,
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);
        await Assert.That(preview.Success).IsTrue();
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var record = await Store.GetAsync(preview.Job!.JobId, CancellationToken.None);
        var data = JobJson.Deserialize<JobPreview>(record!.PreviewJson)!;

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "rename_views",
                Mode = JobMode.Apply,
                SpecJson = specJson,
                PreviewDigest = data.Digest,
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));

        var names = CollectViewNames();
        await Assert.That(names.Contains("SC_Plan_01")).IsTrue();
        await Assert.That(names.Contains("SC_Plan_02")).IsTrue();
        await Assert.That(names.Contains("SC_Plan_03")).IsTrue();
    }

    [Test]
    public async Task Job_InvalidNewPreview_KeepsOldPreviewReady()
    {
        var first = Runner.Start(RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 50)), Doc);
        await Assert.That(first.Success).IsTrue();
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        var invalid = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "rename_views",
                Mode = JobMode.Preview,
                SpecJson = "{\"select\":{\"levelId\":1},\"rename\":{\"prefix\":\"X_\"}}",
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);

        await Assert.That(invalid.Success).IsFalse();
        await Assert.That(invalid.Error!.Code).IsEqualTo(JobErrorCodes.InvalidSpec);
        var old = await Store.GetAsync(first.Job!.JobId, CancellationToken.None);
        await Assert.That(old!.State).IsEqualTo(JobState.PreviewReady);
    }

    [Test]
    public async Task Job_OnErrorAbort_WithPreviewFailures_AppliesNothing()
    {
        var walls = new List<long>();
        _transactions!.RunInTransaction(Doc, "Seed abort preview walls", doc =>
        {
            for (var index = 0; index < 3; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, ElementIdCompat.Create(_levelId), 40 + index);
                if (wall is null)
                {
                    return;
                }

                walls.Add(wall.Id.GetValue());
            }
        });

        if (walls.Count != 3)
        {
            Skip.Test("The project template has no wall type to seed walls");
        }

        var specJson =
            "{\"select\":{\"elementIds\":[" + string.Join(",", walls) +
            "]},\"parameter\":\"NoSuchParameter\",\"value\":\"x\",\"scope\":\"instance\"}";
        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Preview,
                SpecJson = specJson,
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);
        await Assert.That(preview.Success).IsTrue();
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var record = await Store.GetAsync(preview.Job!.JobId, CancellationToken.None);
        var data = JobJson.Deserialize<JobPreview>(record!.PreviewJson)!;

        var apply = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Apply,
                SpecJson = specJson,
                PreviewDigest = data.Digest,
                Options = Options(JobSavePolicy.Never, 50),
                OptionsOverride = new JobOptionsOverride { OnError = JobOnError.Abort }
            },
            Doc);

        await Assert.That(apply.Success).IsFalse();
        await Assert.That(apply.Error!.Code).IsEqualTo(JobErrorCodes.AbortedByPolicy);
        var after = await Store.GetAsync(preview.Job.JobId, CancellationToken.None);
        await Assert.That(after!.State).IsEqualTo(JobState.PreviewReady);
    }

    [Test]
    public async Task Job_OnErrorAbort_RollbackOnError_UndoesPreviousBatches()
    {
        var failing = new FailAtAction(new RenameViewsJobAction(), failAtSeq: 2);
        var runner = CreateRunner(failing);
        var preview = runner.Start(
            RenameViewSpec(JobMode.Preview, options: Options(JobSavePolicy.Never, 1)),
            Doc);
        await Assert.That(preview.Success).IsTrue();
        runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var previewRecord = await Store.GetAsync(preview.Job!.JobId, CancellationToken.None);
        var digest = JobJson.Deserialize<JobPreview>(previewRecord!.PreviewJson)!.Digest;

        // The apply call supplies the policies; the preview stored none of them.
        var apply = runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "rename_views",
                Mode = JobMode.Apply,
                SpecJson = RenameSpec,
                PreviewDigest = digest,
                Options = Options(JobSavePolicy.Never, 1),
                OptionsOverride = new JobOptionsOverride
                {
                    OnError = JobOnError.Abort,
                    RollbackOnError = true
                }
            },
            Doc);

        await Assert.That(apply.Success).IsTrue();
        var result = runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));

        await Assert.That(result.State).IsEqualTo(JobState.RolledBack);
        var names = CollectViewNames();
        await Assert.That(names.Contains("SC_Job_View_1")).IsTrue();
        await Assert.That(names.Any(name => name.StartsWith("SC_Job_Fixed_", StringComparison.Ordinal))).IsFalse();

        var items = await Store.LoadAllItemsAsync(preview.Job.JobId, CancellationToken.None);
        await Assert.That(items.First(item => item.Seq == 1).State).IsEqualTo(JobItemState.Undone);
        await Assert.That(items.First(item => item.Seq == 2).State).IsEqualTo(JobItemState.Failed);
    }

    [Test]
    public async Task Job_LargeSelection_RequiresExplicitAcknowledgement()
    {
        // Even a preview that carries the flag must not satisfy the apply-time gate.
        var options = new JobOptions
        {
            SavePolicy = JobSavePolicy.Never,
            BatchSize = 50,
            SliceBudgetMs = 200,
            MaxItems = 1000,
            AllowLargeJob = true
        };
        var preview = Runner.Start(RenameViewSpec(JobMode.Preview, options: options), Doc);
        await Assert.That(preview.Success).IsTrue();
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        var previewRecord = await Store.GetAsync(preview.Job!.JobId, CancellationToken.None);
        var digest = JobJson.Deserialize<JobPreview>(previewRecord!.PreviewJson)!.Digest;

        var blockedOptions = new JobOptions
        {
            SavePolicy = JobSavePolicy.Never,
            BatchSize = 50,
            SliceBudgetMs = 200,
            MaxItems = 1000,
            AllowLargeJob = false
        };
        var blocked = Runner.Start(
            RenameViewSpec(JobMode.Apply, digest, blockedOptions),
            Doc);

        await Assert.That(blocked.Success).IsFalse();
        await Assert.That(blocked.Error!.Code).IsEqualTo(JobErrorCodes.ConfirmationRequired);
        var stored = await Store.GetAsync(preview.Job.JobId, CancellationToken.None);
        await Assert.That(stored!.State).IsEqualTo(JobState.PreviewReady);

        var allowed = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "rename_views",
                Mode = JobMode.Apply,
                SpecJson = RenameSpec,
                PreviewDigest = digest,
                Options = options,
                OptionsOverride = new JobOptionsOverride { AllowLargeJob = true }
            },
            Doc);
        await Assert.That(allowed.Success).IsTrue();
        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        await Assert.That(result.State).IsEqualTo(JobState.Completed);
    }

    [Test]
    public async Task Job_RetryFailed_ReevaluatesPreviewAndPreservesCause()
    {
        var walls = new List<long>();
        _transactions!.RunInTransaction(Doc, "Seed retry walls", doc =>
        {
            for (var index = 0; index < 3; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, ElementIdCompat.Create(_levelId), 30 + index);
                if (wall is null)
                {
                    return;
                }

                walls.Add(wall.Id.GetValue());
            }
        });

        if (walls.Count != 3)
        {
            Skip.Test("The project template has no wall type to seed walls");
        }

        var specJson =
            "{\"select\":{\"elementIds\":[" + string.Join(",", walls) +
            "]},\"parameter\":\"NoSuchParameter\",\"value\":\"x\",\"scope\":\"instance\"}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Preview,
                SpecJson = specJson,
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);
        await Assert.That(preview.Success).IsTrue();
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var previewRecord = await Store.GetAsync(preview.Job!.JobId, CancellationToken.None);
        var data = JobJson.Deserialize<JobPreview>(previewRecord!.PreviewJson)!;
        await Assert.That(data.FailedCount).IsEqualTo(3);

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "set_parameters",
                Mode = JobMode.Apply,
                SpecJson = specJson,
                PreviewDigest = data.Digest,
                Options = Options(JobSavePolicy.Never, 50)
            },
            Doc);
        var applied = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        await Assert.That(applied.State).IsEqualTo(JobState.CompletedWithFailures);

        var retry = Runner.RetryFailed(Doc, preview.Job.JobId);

        await Assert.That(retry.Attempted).IsEqualTo(3);
        await Assert.That(retry.Reset).IsEqualTo(0);
        await Assert.That(retry.StillFailed).IsEqualTo(3);
        await Assert.That(retry.ErrorCode).IsEqualTo("parameter_not_found");
        await Assert.That(retry.ErrorDetail).Contains("NoSuchParameter");

        var items = await Store.LoadAllItemsAsync(preview.Job.JobId, CancellationToken.None);
        await Assert.That(items.All(item => item.Attempts > 0)).IsTrue();

        var report = new JobReportBuilder(Store, _actions!)
            .Build(Doc, preview.Job.JobId, offset: 0, limit: 10, readBack: true, out _);
        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Errors.Count).IsEqualTo(1);
        await Assert.That(report.Errors[0].ErrorCode).IsEqualTo("parameter_not_found");
        await Assert.That(report.Errors[0].Count).IsEqualTo(3);
    }

    [Test]
public async Task Job_StartToolSchema_IsTypedAndKeepsUnknownSpecKeysForActionValidation()
    {
        var tool = new StartJobTool(Runner, Store, new StubRevitContext(Doc));
        using var schema = JsonDocument.Parse(tool.Definition.InputSchemaJson);

        var specProperties = schema.RootElement
            .GetProperty("properties")
            .GetProperty("spec")
            .GetProperty("properties");
        await Assert.That(specProperties.TryGetProperty("select", out _)).IsTrue();
        await Assert.That(specProperties.TryGetProperty("rename", out _)).IsTrue();
        await Assert.That(specProperties.TryGetProperty("mappings", out _)).IsTrue();

        var known = SmartCon.Core.Services.Agent.AgentToolSchemaValidator.Validate(
            tool.Definition.InputSchemaJson,
            "{\"action\":\"rename_views\",\"spec\":{\"select\":{\"type\":\"Legend\"}," +
            "\"rename\":{\"prefix\":\"X_\"}}}");
        await Assert.That(known.Count).IsEqualTo(0);

        // Unknown spec keys must reach the action-specific validator (job_spec_invalid
        // with the allowed-key list), not be rejected by the generic schema.
        var unknown = SmartCon.Core.Services.Agent.AgentToolSchemaValidator.Validate(
            tool.Definition.InputSchemaJson,
            "{\"action\":\"rename_views\",\"spec\":{\"typo\":1}}");
        await Assert.That(unknown.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Job_ReportSummary_OmitsItemsAndKeepsTotals()
    {
        var job = PrepareApplyJob(JobSavePolicy.Never, batchSize: 50);
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));

        var tool = new GetJobReportTool(
            Store,
            new JobReportBuilder(Store, _actions!),
            new StubRevitContext(Doc));
        var context = new AgentToolContext(
            new InlineAgentAwaitableEvent(),
            DocumentId,
            Guid.Empty);

        using var args = JsonDocument.Parse(
            "{\"jobId\":\"" + job.JobId.ToString("D") +
            "\",\"readBack\":false,\"includeItems\":false}");
        var result = await tool.ExecuteAsync(context, args.RootElement, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        var hasItems = data.TryGetProperty("items", out var itemsElement);
        await Assert.That(!hasItems || itemsElement.ValueKind == JsonValueKind.Null).IsTrue();
        await Assert.That(data.GetProperty("readBack").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("totals").GetProperty("done").GetInt32())
            .IsEqualTo(ViewCount);
        await Assert.That(data.GetProperty("cancelled").GetBoolean()).IsFalse();
        await Assert.That(result.Message).Contains("read-back skipped");
    }

    private sealed class InlineAgentAwaitableEvent : IAgentAwaitableEvent
    {
        private static readonly object App = new();

        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default) =>
            Task.FromResult(funcWithApp(App));

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            actionWithApp(App);
            return Task.CompletedTask;
        }

        public void ProcessQueue(object revitApp)
        {
        }

        public void Initialize(Action onRaise)
        {
        }
    }

    private sealed class FailAtAction : IJobAction
    {
        private readonly IJobAction _inner;
        private readonly int _failAtSeq;

        public FailAtAction(IJobAction inner, int failAtSeq)
        {
            _inner = inner;
            _failAtSeq = failAtSeq;
        }

        public string Name => _inner.Name;

        public JobActionScope Scope => _inner.Scope;

        public int MaxUnits => _inner.MaxUnits;

        public JobExpansion Expand(JobActionContext context, string specJson) =>
            _inner.Expand(context, specJson);

        public JobUnitPreview Preview(JobActionContext context, JobUnit unit) =>
            _inner.Preview(context, unit);

        public JobUnitApply Apply(
            JobActionContext context,
            JobUnit unit,
            JobUnitPreview preview,
            int attempt,
            Action<string>? persistUndo) =>
            unit.Seq == _failAtSeq
                ? new JobUnitApply
                {
                    ErrorCode = "simulated_failure",
                    ErrorDetail = "Injected failure for the onError=abort test"
                }
                : _inner.Apply(context, unit, preview, attempt, persistUndo);

        public JobUnitVerification Verify(
            JobActionContext context,
            JobUnit unit,
            JobUnitPreview preview) =>
            _inner.Verify(context, unit, preview);

        public JobUnitUndo Undo(
            JobActionContext context,
            JobUnit unit,
            string? undoJson,
            bool dryRun) =>
            _inner.Undo(context, unit, undoJson, dryRun);
    }
}