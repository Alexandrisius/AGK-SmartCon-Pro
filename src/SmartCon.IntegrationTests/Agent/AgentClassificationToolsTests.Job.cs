using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// The classification check of the set_parameters job action: the preview
/// fails a unit with the typed reason, the apply slices check again against the
/// table as it is then. Partial of <see cref="AgentClassificationToolsTests"/>.
/// </summary>
public sealed partial class AgentClassificationToolsTests
{
    private const string OnlyShellTable =
        "10\tShell\t1\t\n" +
        "10.10\tWalls\t2\t\n";

    private string? _jobDatabasePath;
    private SqliteAgentJobStore? _jobStore;
    private JobRunner? _jobRunner;

    private SqliteAgentJobStore JobStore => _jobStore!;

    private JobRunner Jobs => _jobRunner!;

    private void CreateJobRunner()
    {
        _jobDatabasePath = Path.Combine(
            ClassificationFixtures.Directory(), "classification-job-" + Guid.NewGuid().ToString("N") + ".db");
        var database = new AgentDatabase(_jobDatabasePath);
        database.Initialize();
        _jobStore = new SqliteAgentJobStore(database);
        _jobRunner = new JobRunner(_jobStore, new IJobAction[] { new SetParametersJobAction() }, _transactions!);
    }

    private void CleanupJobDatabase()
    {
        if (_jobDatabasePath is null || !File.Exists(_jobDatabasePath))
            return;

        try
        {
            File.Delete(_jobDatabasePath);
        }
        catch (IOException)
        {
            // Best effort: a still-locked database file is harmless.
        }
    }

    private List<long> SeedWalls(int count)
    {
        var ids = new List<long>();
        _transactions!.RunInTransaction(Doc, "Seed walls for a classification job", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            for (var index = 0; index < count; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, level.Id, index * 0.5);
                if (wall is not null)
                    ids.Add(wall.Id.GetValue());
            }
        });
        return ids;
    }

    private static string CodeJobSpec(IEnumerable<long> wallIds, string value, bool allowUnverified = false) =>
        "{\"select\":{\"elementIds\":[" + string.Join(",", wallIds) +
        "]},\"builtInParameter\":\"UNIFORMAT_CODE\",\"scope\":\"type\",\"value\":\"" + value + "\"" +
        (allowUnverified ? ",\"allowUnverifiedClassification\":true" : string.Empty) + "}";

    private static JobOptions RunOptions() => new()
    {
        SavePolicy = JobSavePolicy.Never,
        BatchSize = 50,
        SliceBudgetMs = 5000,
        MaxItems = 1000
    };

    private JobSpec SpecFor(JobMode mode, string specJson, string? digest = null) => new()
    {
        DocumentId = AgentDocumentIdentity.GetDocumentId(Doc),
        Action = "set_parameters",
        Mode = mode,
        PreviewDigest = digest,
        SpecJson = specJson,
        Options = RunOptions()
    };

    private static JobStartResult StartOk(JobStartResult start)
    {
        if (!start.Success)
            throw new InvalidOperationException("The job did not start: " + start.Error?.Code + " " + start.Error?.Message);
        return start;
    }

    private async Task<JobRecord> PreviewJob(string specJson)
    {
        var start = StartOk(Jobs.Start(SpecFor(JobMode.Preview, specJson), Doc));
        Jobs.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
        var record = await JobStore.GetAsync(start.Job!.JobId, CancellationToken.None);
        return record!;
    }

    private JobSliceResult ApplyJob(JobRecord preview, string specJson)
    {
        StartOk(Jobs.Start(SpecFor(JobMode.Apply, specJson, PreviewOf(preview).Digest), Doc));
        return Jobs.RunToCompletion(Doc, TimeSpan.FromMinutes(1));
    }

    private static JobPreview PreviewOf(JobRecord record) =>
        JobJson.Deserialize<JobPreview>(record.PreviewJson)!;

    private async Task<List<JobItem>> ItemsOf(JobRecord job) =>
        (await JobStore.LoadAllItemsAsync(job.JobId, CancellationToken.None)).ToList();

    private string? CodeOfWallType(ElementId typeId) =>
        Doc.GetElement(typeId)?.get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn)?.AsString();

    private ElementId FirstWallTypeId() =>
        new FilteredElementCollector(Doc).OfClass(typeof(WallType)).FirstElementId();

    // ── tests ───────────────────────────────────────────────────────────

    [Test]
    public async Task Job_WithoutTable_PreviewFailsEveryUnitWithTheTypedReason()
    {
        var wallIds = SeedWalls(3);
        if (wallIds.Count == 0)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");
        CreateJobRunner();
        var typeId = FirstWallTypeId();

        var job = await PreviewJob(CodeJobSpec(wallIds, "20.10.10"));
        var items = await ItemsOf(job);

        await Assert.That(items.Count).IsEqualTo(wallIds.Count);
        await Assert.That(items.All(item => item.State == JobItemState.Failed)).IsTrue();
        await Assert.That(items.All(item => item.ErrorCode == "classification_table_not_loaded")).IsTrue();
        await Assert.That(items.All(item =>
            (item.ErrorDetail ?? string.Empty).Contains("No classification table is loaded"))).IsTrue();

        var preview = PreviewOf(job);
        await Assert.That(preview.FailedCount).IsEqualTo(wallIds.Count);
        await Assert.That(preview.WillChangeCount).IsEqualTo(0);
        await Assert.That(preview.Sample.Any(sample =>
            sample.ErrorCode == "classification_table_not_loaded"
            && (sample.ErrorDetail ?? string.Empty).Contains("load_classification_table"))).IsTrue();
        await Assert.That(CodeOfWallType(typeId) ?? string.Empty).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Job_WithoutTable_OptIn_PreviewsWithAWarningAndApplies()
    {
        var wallIds = SeedWalls(2);
        if (wallIds.Count == 0)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");
        CreateJobRunner();
        var typeId = FirstWallTypeId();
        var spec = CodeJobSpec(wallIds, "20.10.10", allowUnverified: true);

        var job = await PreviewJob(spec);

        var preview = PreviewOf(job);
        await Assert.That(preview.FailedCount).IsEqualTo(0);
        await Assert.That(preview.WillChangeCount).IsEqualTo(wallIds.Count);
        await Assert.That(preview.Warnings.Any(warning => warning.Contains("allowUnverifiedClassification=true"))).IsTrue();
        await Assert.That(preview.Warnings.Any(warning =>
            warning.Contains("written unverified (classification_table_not_loaded)"))).IsTrue();
        var items = await ItemsOf(job);
        await Assert.That(items.All(item => item.State == JobItemState.Previewed)).IsTrue();
        await Assert.That(items.All(item => (item.ArgsJson ?? string.Empty).Contains("\"allowUnverifiedClassification\":true"))).IsTrue();

        var applied = ApplyJob(job, spec);

        await Assert.That(applied.Error).IsNull();
        // Both walls share one type: the first unit writes it, the second finds the value in place.
        var done = await ItemsOf(job);
        await Assert.That(done.All(item => item.State is JobItemState.Done or JobItemState.Skipped)).IsTrue();
        await Assert.That(done.Count(item => item.State == JobItemState.Done)).IsEqualTo(1);
        await Assert.That(CodeOfWallType(typeId)).IsEqualTo("20.10.10");
    }

    [Test]
    public async Task Job_TableLoaded_PreviewsAppliesAndRevitFillsTheDescription()
    {
        var wallIds = SeedWalls(2);
        if (wallIds.Count == 0)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");
        await Assert.That(Load(SampleFile("job-table.txt"), dryRun: false).Success).IsTrue();
        CreateJobRunner();
        var typeId = FirstWallTypeId();
        var spec = CodeJobSpec(wallIds, "20.10.10");

        var job = await PreviewJob(spec);
        await Assert.That(PreviewOf(job).WillChangeCount).IsEqualTo(wallIds.Count);

        var applied = ApplyJob(job, spec);

        await Assert.That(applied.Error).IsNull();
        await Assert.That(CodeOfWallType(typeId)).IsEqualTo("20.10.10");
        await Assert.That(Doc.GetElement(typeId)!.get_Parameter(AgentParameterResolver.AssemblyDescriptionBuiltIn)!.AsString())
            .IsEqualTo("Pipes");

        var report = new JobReportBuilder(JobStore, new IJobAction[] { new SetParametersJobAction() })
            .Build(Doc, job.JobId, 0, 10, readBack: false, out _);
        await Assert.That(report!.Truthful).IsTrue();
        await Assert.That(report.Totals.Done + report.Totals.Skipped).IsEqualTo(wallIds.Count);
    }

    [Test]
    public async Task Job_Apply_ChecksTheTableAgainInsteadOfTrustingThePreview()
    {
        var wallIds = SeedWalls(2);
        if (wallIds.Count == 0)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");
        await Assert.That(Load(SampleFile("job-recheck-a.txt"), dryRun: false).Success).IsTrue();
        CreateJobRunner();
        var typeId = FirstWallTypeId();
        var spec = CodeJobSpec(wallIds, "20.10.10");

        var job = await PreviewJob(spec);
        await Assert.That(PreviewOf(job).WillChangeCount).IsEqualTo(wallIds.Count);

        // The classifier changes between the preview and the apply.
        await Assert.That(Load(SampleFile("job-recheck-b.txt", OnlyShellTable), dryRun: false).Success).IsTrue();

        var applied = ApplyJob(job, spec);

        await Assert.That(applied.Error).IsNull();
        var items = await ItemsOf(job);
        await Assert.That(items.All(item => item.State == JobItemState.Failed)).IsTrue();
        await Assert.That(items.All(item => item.ErrorCode == "classification_code_not_found")).IsTrue();
        await Assert.That((items[0].ErrorDetail ?? string.Empty).Contains("run the preview again")).IsTrue();
        await Assert.That(CodeOfWallType(typeId) ?? string.Empty).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Job_FlagOfTheWrongShape_IsAnInvalidSpec()
    {
        var wallIds = SeedWalls(1);
        if (wallIds.Count == 0)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");
        CreateJobRunner();

        var start = Jobs.Start(
            SpecFor(JobMode.Preview, CodeJobSpec(wallIds, "20.10.10").TrimEnd('}') +
                                     ",\"allowUnverifiedClassification\":\"yes\"}"),
            Doc);

        await Assert.That(start.Success).IsFalse();
        await Assert.That(start.Error!.Code).IsEqualTo(JobErrorCodes.InvalidSpec);
        await Assert.That(start.Error.Message).Contains("allowUnverifiedClassification must be true or false");
    }
}
