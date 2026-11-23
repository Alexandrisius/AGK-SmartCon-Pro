using System.Windows.Threading;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Batch (multi-unit) family jobs through the production agent graph. Regression
/// coverage for the C2-01 pattern of the live benchmark: every unit after the
/// first inline one was refused with permission_denied "project document is
/// read-only", because the headless host drove the job pump off the Revit API
/// thread (Revit reports an untouched document as read-only from another thread).
/// </summary>
public sealed partial class AgentFamilyJobBatchTests : RevitApiTest
{
    private const int FamilyCount = 3;
    private const string FamilyPrefix = "SC_JobBatch_";

    private static readonly string MapSpec =
        "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"mappings\":[" +
        "{\"source\":{\"name\":\"" + FamilySeed.WidthParam + "\"}," +
        "\"target\":{\"name\":\"" + FamilySeed.HeightParam + "\"}}]}";

    private Document? _document;
    private readonly List<FamilySeed> _seeds = new();

    private Document Doc => _document!;

    private string DocumentId => AgentDocumentIdentity.GetDocumentId(Doc);

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
    }

    [After(Test)]
    public void Cleanup()
    {
        foreach (var familyDoc in OpenFamilyDocs)
        {
            if (familyDoc.IsValidObject)
            {
                try
                {
                    familyDoc.Close(false);
                }
                catch (Exception)
                {
                    // Best effort: a failed assertion may have left a document Revit refuses to close.
                }
            }
        }

        OpenFamilyDocs.Clear();

        foreach (var seed in _seeds)
        {
            seed.Dispose();
        }

        _seeds.Clear();
        Doc.Close(false);
    }

    /// <summary>
    /// Control on the Revit thread: the first unit fails its plan (no source
    /// parameter) and the following units are still previewed — the
    /// family-document lifecycle of one unit does not leak into the next.
    /// </summary>
    [Test]
    public async Task BatchPreview_DirectRunner_FirstUnitPlanFailure_FollowingUnitsStillPreview()
    {
        if (!SeedFamilies())
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        DropWidthParameter(FamilyPrefix + "1");

        var databasePath = NewDatabase(out var store);
        var runner = new JobRunner(
            store,
            new IJobAction[] { new MapFamilyParametersJobAction() },
            new RevitTransactionService(new StubRevitContext(Doc)));

        var preview = runner.Start(PreviewSpec(DocumentId, MapSpec), Doc);
        await Assert.That(preview.Success).IsTrue();
        var result = runner.RunToCompletion(Doc, TimeSpan.FromMinutes(2));
        await Assert.That(result.State).IsEqualTo(JobState.PreviewReady);

        var items = await store.LoadAllItemsAsync(preview.Job!.JobId, CancellationToken.None);
        await Assert.That(items.Count(item => item.ErrorCode == "family_mapping_invalid")).IsEqualTo(1);
        await Assert.That(items.Count(item => item.ErrorCode == JobErrorCodes.ItemFailed)).IsEqualTo(0);
        await Assert.That(items.Count(item => item.State == JobItemState.Previewed)).IsEqualTo(2);
        await Assert.That(Doc.Application.Documents.Cast<Document>().Count()).IsEqualTo(1);

        TryDeleteDatabase(databasePath);
    }

    /// <summary>
    /// The C2-01 regression: the same batch through the tool path (executor on a
    /// thread-pool thread, Revit dispatcher pumped, job pump raised by the tools).
    /// Before the fix units 2 and 3 failed with job_item_failed "permission_denied:
    /// The project document is read-only".
    /// </summary>
    [Test]
    public async Task Batch_ToolPath_FirstUnitPlanFailure_FollowingUnitsStillPreview()
    {
        if (!SeedFamilies())
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        DropWidthParameter(FamilyPrefix + "1");

        using var host = CreateHost();

        // One call carries the finished preview and its causes (C2-05): the first
        // unit fails its plan, the two others are previewed.
        var start = Pump(() => host.Executor.ExecuteAsync("start_job", PreviewArgs(MissingTarget: false), CancellationToken.None));
        await Assert.That(start.Success).IsTrue();
        var data = start.Data!.Value;
        await Assert.That(data.GetProperty("state").GetString()).IsEqualTo("PreviewReady");
        await Assert.That(data.GetProperty("willChange").GetInt32()).IsEqualTo(2);
        await Assert.That(data.GetProperty("failed").GetInt32()).IsEqualTo(1);

        // Causes are grouped by the code of the cause, never by job_item_failed.
        var causes = data.GetProperty("failureCauses").EnumerateArray().ToList();
        await Assert.That(causes.Count).IsEqualTo(1);
        await Assert.That(causes[0].GetProperty("errorCode").GetString()).IsEqualTo("family_mapping_invalid");
        await Assert.That(causes[0].GetProperty("count").GetInt32()).IsEqualTo(1);
        await Assert.That((start.Data!.Value.GetRawText()).Contains("job_item_failed", StringComparison.Ordinal)).IsFalse();

        // A partially failed preview says what is applied and what is not (C2-04).
        var next = data.GetProperty("next").GetString()!;
        await Assert.That(next.StartsWith("apply: call start_job with dryRun=false", StringComparison.Ordinal)).IsTrue();
        await Assert.That(next.Contains("Only the 2 unit(s) that passed the preview are applied", StringComparison.Ordinal)).IsTrue();
        await Assert.That(start.Message!.Contains("1 failed", StringComparison.Ordinal)).IsTrue();

        var status = Pump(() => host.Executor.ExecuteAsync(
            "get_job_status",
            Parse(new { waitSeconds = 5 }),
            CancellationToken.None));
        await Assert.That(status.Success).IsTrue();
        await Assert.That(status.Message!.Contains("Causes: family_mapping_invalid ×1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(status.Data!.Value.GetProperty("failureCauses").GetArrayLength()).IsEqualTo(1);

        var report = Pump(() => host.Executor.ExecuteAsync(
            "get_job_report",
            Parse(new { includeItems = true, readBack = false }),
            CancellationToken.None));
        await Assert.That(report.Message!.Contains("family_mapping_invalid ×1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(report.Message!.Contains("job_item_failed", StringComparison.Ordinal)).IsFalse();

        await Assert.That(Doc.IsReadOnly).IsFalse();
        await Assert.That(Doc.Application.Documents.Cast<Document>().Count()).IsEqualTo(1);
    }

    /// <summary>
    /// A valid batch: start_job returns the ready preview in the same call, and the
    /// apply call returns the finished job — preview and apply of three families in
    /// two calls instead of six.
    /// </summary>
    [Test]
    public async Task Batch_ToolPath_AllUnitsValid_PreviewAndApplyInOneJob()
    {
        if (!SeedFamilies())
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        using var host = CreateHost();

        var start = Pump(() => host.Executor.ExecuteAsync("start_job", PreviewArgs(MissingTarget: false), CancellationToken.None));
        await Assert.That(start.Success).IsTrue();
        await Assert.That(start.Data!.Value.GetProperty("state").GetString()).IsEqualTo("PreviewReady");
        await Assert.That(start.Data.Value.GetProperty("willChange").GetInt32()).IsEqualTo(FamilyCount);
        await Assert.That(start.Data.Value.GetProperty("failed").GetInt32()).IsEqualTo(0);
        // No failures: the serializer omits the null causes array.
        await Assert.That(start.Data.Value.TryGetProperty("failureCauses", out var causesProperty) &&
                          causesProperty.ValueKind != System.Text.Json.JsonValueKind.Null).IsFalse();
        await Assert.That(start.Message!.StartsWith("Preview ready:", StringComparison.Ordinal)).IsTrue();
        var digest = start.Data.Value.GetProperty("previewDigest").GetString()!;

        var apply = Pump(() => host.Executor.ExecuteAsync(
            "start_job",
            Parse(new { action = "map_family_parameters", dryRun = false, previewDigest = digest, includeSample = false }),
            CancellationToken.None));
        await Assert.That(apply.Success).IsTrue();
        await Assert.That(apply.Data!.Value.GetProperty("state").GetString()).IsEqualTo("Completed");
        await Assert.That(apply.Message!.StartsWith("Job finished: Completed —", StringComparison.Ordinal)).IsTrue();

        foreach (var seed in _seeds)
        {
            var width = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
            var height = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.HeightParam);
            await Assert.That(width).IsNotNull();
            await Assert.That(height).IsNotNull();
            await Assert.That(Math.Abs(height!.AsDouble() - width!.AsDouble()) < 1e-9).IsTrue();
        }

        await Assert.That(Doc.IsReadOnly).IsFalse();
        await Assert.That(Doc.Application.Documents.Cast<Document>().Count()).IsEqualTo(1);
    }

    /// <summary>
    /// A preview whose units all failed has nothing to apply: neither start_job nor
    /// get_job_status may present it as applicable (C2-04).
    /// </summary>
    [Test]
    public async Task Batch_ToolPath_AllUnitsFailPreview_NeverSuggestsApply()
    {
        if (!SeedFamilies())
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        using var host = CreateHost();

        var start = Pump(() => host.Executor.ExecuteAsync("start_job", PreviewArgs(MissingTarget: true), CancellationToken.None));
        await Assert.That(start.Success).IsTrue();
        var data = start.Data!.Value;
        await Assert.That(data.GetProperty("state").GetString()).IsEqualTo("PreviewReady");
        await Assert.That(data.GetProperty("failed").GetInt32()).IsEqualTo(FamilyCount);
        await Assert.That(data.GetProperty("willChange").GetInt32()).IsEqualTo(0);

        var nothing = "nothing to apply: all " + FamilyCount + " unit(s) failed the preview";
        await Assert.That(start.Message!.Contains(nothing, StringComparison.Ordinal)).IsTrue();
        await Assert.That(start.Message!.Contains("Apply with dryRun=false", StringComparison.Ordinal)).IsFalse();
        await Assert.That(data.GetProperty("next").GetString()!.StartsWith(nothing, StringComparison.Ordinal)).IsTrue();

        // Causes travel with every status, with or without includeSample.
        var status = Pump(() => host.Executor.ExecuteAsync(
            "get_job_status",
            Parse(new { waitSeconds = 0 }),
            CancellationToken.None));
        await Assert.That(status.Data!.Value.GetProperty("next").GetString()!.StartsWith(nothing, StringComparison.Ordinal)).IsTrue();
        await Assert.That(status.Message!.Contains("Causes: family_mapping_invalid ×" + FamilyCount, StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A refusal by the document is not a property of one unit: the job stops with
    /// one job-level error naming the cause, and the units that were not attempted
    /// are not reported as failed.
    /// </summary>
    [Test]
    public async Task Runner_DocumentRefusalInPreview_StopsJobWithOneError()
    {
        var databasePath = NewDatabase(out var store);
        var runner = new JobRunner(
            store,
            new IJobAction[] { new RefusingJobAction { UnitCount = 4, RefuseAtSeq = 2 } },
            new RevitTransactionService(new StubRevitContext(Doc)));

        var start = runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "refusing_probe",
                Mode = JobMode.Preview,
                SpecJson = "{}",
                Options = Options()
            },
            Doc);
        await Assert.That(start.Success).IsTrue();
        var result = runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));

        await Assert.That(result.State).IsEqualTo(JobState.Failed);
        var job = await store.GetAsync(start.Job!.JobId, CancellationToken.None);
        await Assert.That(job!.ErrorCode).IsEqualTo(JobErrorCodes.DocumentReadOnly);
        await Assert.That(job.ErrorDetail!.Contains("while unit 'Unit 2' was being previewed", StringComparison.Ordinal)).IsTrue();
        await Assert.That(job.ErrorDetail!.Contains("Nothing was applied; 2 unit(s) were not attempted", StringComparison.Ordinal)).IsTrue();

        var items = await store.LoadAllItemsAsync(start.Job.JobId, CancellationToken.None);
        await Assert.That(items.Single(item => item.Seq == 1).State).IsEqualTo(JobItemState.Previewed);
        await Assert.That(items.Single(item => item.Seq == 2).ErrorCode).IsEqualTo(RefusingJobAction.RefusalCode);
        await Assert.That(items.Single(item => item.Seq == 3).State).IsEqualTo(JobItemState.Pending);
        await Assert.That(items.Single(item => item.Seq == 4).State).IsEqualTo(JobItemState.Pending);

        TryDeleteDatabase(databasePath);
    }

    /// <summary>
    /// The same refusal while applying: units applied before it stay applied and
    /// the job-level error says so; the rest is not attempted.
    /// </summary>
    [Test]
    public async Task Runner_DocumentRefusalInApply_KeepsAppliedUnitsAndStopsJob()
    {
        var databasePath = NewDatabase(out var store);
        var previewRunner = new JobRunner(
            store,
            new IJobAction[] { new RefusingJobAction { UnitCount = 4, RefuseAtSeq = 99 } },
            new RevitTransactionService(new StubRevitContext(Doc)));

        var start = previewRunner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "refusing_probe",
                Mode = JobMode.Preview,
                SpecJson = "{}",
                Options = Options()
            },
            Doc);
        previewRunner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var job = await store.GetAsync(start.Job!.JobId, CancellationToken.None);
        await Assert.That(job!.State).IsEqualTo(JobState.PreviewReady);
        var digest = JobJson.Deserialize<JobPreview>(job.PreviewJson)!.Digest;

        var applyRunner = new JobRunner(
            store,
            new IJobAction[] { new RefusingJobAction { UnitCount = 4, RefuseAtSeq = 2 } },
            new RevitTransactionService(new StubRevitContext(Doc)));

        var apply = applyRunner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "refusing_probe",
                Mode = JobMode.Apply,
                SpecJson = "{}",
                PreviewDigest = digest,
                Options = Options()
            },
            Doc);
        await Assert.That(apply.Success).IsTrue();
        var result = applyRunner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));

        await Assert.That(result.State).IsEqualTo(JobState.Failed);
        job = await store.GetAsync(start.Job.JobId, CancellationToken.None);
        await Assert.That(job!.ErrorCode).IsEqualTo(JobErrorCodes.DocumentReadOnly);
        await Assert.That(job.ErrorDetail!.Contains("1 unit(s) applied before it stay applied; 2 unit(s) were not attempted", StringComparison.Ordinal)).IsTrue();

        var items = await store.LoadAllItemsAsync(start.Job.JobId, CancellationToken.None);
        await Assert.That(items.Single(item => item.Seq == 1).State).IsEqualTo(JobItemState.Done);
        await Assert.That(items.Single(item => item.Seq == 2).ErrorCode).IsEqualTo(RefusingJobAction.RefusalCode);
        await Assert.That(items.Single(item => item.Seq == 3).State).IsEqualTo(JobItemState.Previewed);
        await Assert.That(items.Single(item => item.Seq == 4).State).IsEqualTo(JobItemState.Previewed);

        // The default policy ends the job as failed; nothing is rolled back.
        var events = await EventTypesAsync(store, start.Job.JobId);
        await Assert.That(events.Contains(JobEventTypes.Failed)).IsTrue();
        await Assert.That(events.Contains(JobEventTypes.RollbackStarted)).IsFalse();

        TryDeleteDatabase(databasePath);
    }

    private ProductionAgentHost CreateHost()
    {
        var host = ProductionAgentHost.Create(Doc);
        host.Session.DocumentId = DocumentId;
        return host;
    }

    private static System.Text.Json.JsonElement PreviewArgs(bool MissingTarget) => Parse(new
    {
        action = "map_family_parameters",
        dryRun = true,
        spec = new
        {
            select = new { nameContains = FamilyPrefix },
            mappings = new[]
            {
                new
                {
                    source = new { name = FamilySeed.WidthParam },
                    target = new { name = MissingTarget ? "НетТакойЦели" : FamilySeed.HeightParam }
                }
            }
        }
    });

    private static JobSpec PreviewSpec(string documentId, string specJson) => new()
    {
        DocumentId = documentId,
        Action = "map_family_parameters",
        Mode = JobMode.Preview,
        SpecJson = specJson,
        Options = Options()
    };

    private string NewDatabase(out SqliteAgentJobStore store)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), "sc-jobbatch-" + Guid.NewGuid().ToString("N") + ".db");
        var database = new AgentDatabase(databasePath);
        database.Initialize();
        store = new SqliteAgentJobStore(database);
        return databasePath;
    }

    private bool SeedFamilies()
    {
        for (var index = 1; index <= FamilyCount; index++)
        {
            var seed = FamilySeed.Create(Application, Doc, FamilyPrefix + index, out var reason);
            if (seed is null)
            {
                if (reason is not null)
                {
                    Console.WriteLine("Family seed skipped: " + reason);
                }

                return false;
            }

            _seeds.Add(seed);
        }

        return true;
    }

    /// <summary>Removes the width parameter from one family so its mapping plan fails.</summary>
    private void DropWidthParameter(string familyName)
    {
        Autodesk.Revit.DB.Family? family = null;
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(Autodesk.Revit.DB.Family)))
        {
            if (element is Autodesk.Revit.DB.Family candidate &&
                string.Equals(candidate.Name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                family = candidate;
            }
        }

        if (family is null)
        {
            throw new InvalidOperationException("Seed family '" + familyName + "' was not found");
        }

        var familyDoc = Doc.EditFamily(family);
        try
        {
            using (var transaction = new Transaction(familyDoc, "Seed: drop width parameter"))
            {
                transaction.Start();
                var manager = familyDoc.FamilyManager;
                foreach (var parameter in manager.GetParameters())
                {
                    if (string.Equals(parameter.Definition.Name, FamilySeed.WidthParam, StringComparison.Ordinal))
                    {
                        manager.RemoveParameter(parameter);
                        break;
                    }
                }

                transaction.Commit();
            }

            familyDoc.LoadFamily(Doc, new OverwriteFamilyLoadOptions());
        }
        finally
        {
            familyDoc.Close(false);
        }
    }

    /// <summary>
    /// Runs the call off the Revit thread and pumps the Revit dispatcher until it
    /// completes (the benchmark's PumpOnRevitThread): every Revit API call inside
    /// the call is dispatched back to this thread by RevitThreadEvent.
    /// </summary>
    private static T Pump<T>(Func<Task<T>> start)
    {
        var task = Task.Run(start);
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            task.ContinueWith(
                _ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        return task.GetAwaiter().GetResult();
    }

    private static System.Text.Json.JsonElement Parse(object args)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(args);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JobOptions Options() => new()
    {
        SavePolicy = JobSavePolicy.Never,
        BatchSize = 1,
        SliceBudgetMs = 1500,
        MaxItems = 500
    };

    private static void TryDeleteDatabase(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
