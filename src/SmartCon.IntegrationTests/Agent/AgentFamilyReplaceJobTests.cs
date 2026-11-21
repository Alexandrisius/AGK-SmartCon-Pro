using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Family batch job for replace_family_parameter (#430): the per-family rebind of
/// a family parameter onto an existing shared definition — preview with formula
/// blast radius, apply that keeps per-type values, read-back verification and
/// backup-based rollback. Honest preview failures for spec mismatch and a
/// missing definition are covered too.
/// </summary>
public sealed class AgentFamilyReplaceJobTests : RevitApiTest
{
    private const int FamilyCount = 3;
    private const string FamilyPrefix = "SC_JobRep_";
    private const string TargetName = "Ширина_ТДСК";
    private const string MismatchTargetName = "Текстовый_ТДСК";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private AgentDatabase? _database;
    private string? _databasePath;
    private SqliteAgentJobStore? _store;
    private JobRunner? _runner;
    private string? _sharedFilePrevious;
    private string? _sharedFilePath;
    private readonly List<FamilySeed> _seeds = new();

    private Document Doc => _document!;

    private string DocumentId => AgentDocumentIdentity.GetDocumentId(Doc);

    private SqliteAgentJobStore Store => _store!;

    private JobRunner Runner => _runner!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "sc-jobrep-" + Guid.NewGuid().ToString("N") + ".db");
        _database = new AgentDatabase(_databasePath);
        _database.Initialize();
        _store = new SqliteAgentJobStore(_database);
        _runner = new JobRunner(
            _store,
            new IJobAction[] { new ReplaceFamilyParameterJobAction() },
            _transactions);
    }

    [After(Test)]
    public void Cleanup()
    {
        foreach (var seed in _seeds)
        {
            seed.Dispose();
        }

        _seeds.Clear();
        RestoreSharedParameterFile();
        Doc.Close(false);

        if (_databasePath is not null && File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Test]
    public async Task ReplaceJob_Families_PreviewApplyVerifyAndRollback()
    {
        if (!SeedFamilies(FamilyCount))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFileWithDefinitions();
        var spec = ReplaceSpec(TargetName);

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "replace_family_parameter",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options(batchSize: 2)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.Count).IsEqualTo(FamilyCount);
        await Assert.That(previewData.WillChangeCount).IsEqualTo(FamilyCount);

        // The seeded family drives "Площадь" by a "Ширина * Высота" formula, so the
        // preview must list that parameter in the blast radius (JSON writer escapes
        // Cyrillic — assert through the parsed document).
        var previewItems = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        using var expected = System.Text.Json.JsonDocument.Parse(previewItems[0].ExpectedJson!);
        var entry = expected.RootElement[0];
        await Assert.That(entry.GetProperty("source").GetProperty("name").GetString())
            .IsEqualTo(FamilySeed.WidthParam);
        await Assert.That(entry.GetProperty("target").GetProperty("name").GetString())
            .IsEqualTo(TargetName);
        var formulas = entry.GetProperty("formulasAffected");
        await Assert.That(formulas.EnumerateArray()
            .Any(formula => formula.GetString() == FamilySeed.AreaParam)).IsTrue();

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "replace_family_parameter",
                Mode = JobMode.Apply,
                SpecJson = spec,
                PreviewDigest = previewData.Digest,
                Options = Options(batchSize: 2)
            },
            Doc);

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        await Assert.That(result.State).IsEqualTo(JobState.Completed);

        var items = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(items.Count).IsEqualTo(FamilyCount);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(FamilyCount);

        foreach (var seed in _seeds)
        {
            var target = seed.GetProjectParameter(FamilySeed.Type1, TargetName);
            var width = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
            await Assert.That(target).IsNotNull();
            await Assert.That(width).IsNull();
            await Assert.That(Math.Abs(target!.AsDouble() - FamilySeed.Type1WidthMm / 304.8) < 1e-6).IsTrue();

            var target2 = seed.GetProjectParameter(FamilySeed.Type2, TargetName);
            await Assert.That(target2).IsNotNull();
            await Assert.That(Math.Abs(target2!.AsDouble() - FamilySeed.Type2WidthMm / 304.8) < 1e-6).IsTrue();
        }

        var rollback = Runner.Rollback(Doc, jobId, dryRun: false);

        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);
        await Assert.That(rollback.Restored).IsEqualTo(FamilyCount);
        await Assert.That(rollback.RestoreFailed).IsEqualTo(0);

        foreach (var seed in _seeds)
        {
            var width = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
            var target = seed.GetProjectParameter(FamilySeed.Type1, TargetName);
            await Assert.That(width).IsNotNull();
            await Assert.That(target).IsNull();
            await Assert.That(Math.Abs(width!.AsDouble() - FamilySeed.Type1WidthMm / 304.8) < 1e-6).IsTrue();
        }

        await Assert.That(Doc.Application.Documents.Cast<Document>().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task ReplaceJob_SameNameTarget_RebindsInPlaceAndRollsBack()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        // A same-name target needs the two-step apply: the family source is renamed
        // to a temporary name first, then rebound to the shared definition that
        // carries the original name with a different GUID.
        var targetGuid = UseSharedParameterFileWithSameNameDefinition();

        var spec =
            "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"replacements\":[" +
            "{\"source\":{\"name\":\"" + FamilySeed.WidthParam + "\"},\"target\":{\"name\":\"" +
            FamilySeed.WidthParam + "\",\"guid\":\"" + targetGuid.ToString("D") + "\"}}]}";

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "replace_family_parameter",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = Options(batchSize: 1)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.Count).IsEqualTo(1);
        // Different GUIDs: the unit is a real replacement, not a same-GUID skip.
        await Assert.That(previewData.WillChangeCount).IsEqualTo(1);

        Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "replace_family_parameter",
                Mode = JobMode.Apply,
                SpecJson = spec,
                PreviewDigest = previewData.Digest,
                Options = Options(batchSize: 1)
            },
            Doc);

        var result = Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        await Assert.That(result.State).IsEqualTo(JobState.Completed);
        var items = await Store.LoadAllItemsAsync(jobId, CancellationToken.None);
        await Assert.That(items.Count(item => item.State == JobItemState.Done)).IsEqualTo(1);

        // The parameter keeps its name and per-type values, but is now the shared
        // definition with the target GUID.
        var seed = _seeds[0];
        var width = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
        await Assert.That(width).IsNotNull();
        await Assert.That(width!.IsShared).IsTrue();
        await Assert.That(width.GUID).IsEqualTo(targetGuid);
        await Assert.That(Math.Abs(width.AsDouble() - FamilySeed.Type1WidthMm / 304.8) < 1e-6).IsTrue();

        var width2 = seed.GetProjectParameter(FamilySeed.Type2, FamilySeed.WidthParam);
        await Assert.That(width2).IsNotNull();
        await Assert.That(width2!.IsShared).IsTrue();
        await Assert.That(Math.Abs(width2.AsDouble() - FamilySeed.Type2WidthMm / 304.8) < 1e-6).IsTrue();

        var rollback = Runner.Rollback(Doc, jobId, dryRun: false);

        await Assert.That(rollback.State).IsEqualTo(JobState.RolledBack);
        await Assert.That(rollback.Restored).IsEqualTo(1);

        var restored = seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.IsShared).IsFalse();
        await Assert.That(Math.Abs(restored.AsDouble() - FamilySeed.Type1WidthMm / 304.8) < 1e-6).IsTrue();
    }

    [Test]
    public async Task ReplaceJob_SpecMismatch_FailsPreviewHonestly()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFileWithDefinitions();

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "replace_family_parameter",
                Mode = JobMode.Preview,
                SpecJson = ReplaceSpec(MismatchTargetName),
                Options = Options(batchSize: 1)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.FailedCount).IsEqualTo(1);
        await Assert.That(previewData.Sample.Any(s => s.ErrorDetail!.Contains("spec"))).IsTrue();
    }

    [Test]
    public async Task ReplaceJob_DefinitionNotFound_FailsPreviewHonestly()
    {
        if (!SeedFamilies(1))
        {
            Skip.Test("Family template or family document creation is unavailable");
        }

        UseSharedParameterFileWithDefinitions();

        var preview = Runner.Start(
            new JobSpec
            {
                DocumentId = DocumentId,
                Action = "replace_family_parameter",
                Mode = JobMode.Preview,
                SpecJson = ReplaceSpec("НетТакогоОпределения"),
                Options = Options(batchSize: 1)
            },
            Doc);

        await Assert.That(preview.Success).IsTrue();
        var jobId = preview.Job!.JobId;
        Runner.RunToCompletion(Doc, TimeSpan.FromMinutes(5));

        var previewRecord = await Store.GetAsync(jobId, CancellationToken.None);
        await Assert.That(previewRecord!.State).IsEqualTo(JobState.PreviewReady);
        var previewData = JobJson.Deserialize<JobPreview>(previewRecord.PreviewJson)!;
        await Assert.That(previewData.FailedCount).IsEqualTo(1);
        await Assert.That(previewData.Sample.Any(s => s.ErrorDetail!.Contains("no definition"))).IsTrue();
    }

    private static string ReplaceSpec(string targetName) =>
        "{\"select\":{\"nameContains\":\"" + FamilyPrefix + "\"},\"replacements\":[" +
        "{\"source\":{\"name\":\"" + FamilySeed.WidthParam + "\"},\"target\":{\"name\":\"" + targetName +
        "\"}}]}";

    private bool SeedFamilies(int count)
    {
        for (var index = 1; index <= count; index++)
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

    private static JobOptions Options(int batchSize) => new()
    {
        SavePolicy = JobSavePolicy.Never,
        BatchSize = batchSize,
        SliceBudgetMs = 1500,
        MaxItems = 500
    };

    /// <summary>
    /// Points Revit at a fresh shared parameter file with two definitions: a
    /// length target that matches the seeded "Ширина" and a text target that
    /// must fail the spec pre-check.
    /// </summary>
    private void UseSharedParameterFileWithDefinitions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SmartConFamilyTests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "jobrep-shared-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, string.Empty);
        _sharedFilePrevious = Application.SharedParametersFilename;
        _sharedFilePath = path;
        Application.SharedParametersFilename = path;

        var file = Application.OpenSharedParameterFile();
        var group = file.Groups.Create("СмартКон");
        group.Definitions.Create(new ExternalDefinitionCreationOptions(TargetName, SpecTypeId.Length));
        group.Definitions.Create(
            new ExternalDefinitionCreationOptions(MismatchTargetName, SpecTypeId.String.Text));
    }

    /// <summary>
    /// Points Revit at a fresh shared parameter file with a definition named
    /// exactly as the seeded family parameter ("Ширина") but carrying a different
    /// GUID — the same-name replacement scenario.
    /// </summary>
    private Guid UseSharedParameterFileWithSameNameDefinition()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SmartConFamilyTests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "jobrep-same-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, string.Empty);
        _sharedFilePrevious = Application.SharedParametersFilename;
        _sharedFilePath = path;
        Application.SharedParametersFilename = path;

        var targetGuid = Guid.NewGuid();
        var file = Application.OpenSharedParameterFile();
        var group = file.Groups.Create("СмартКон");
        group.Definitions.Create(new ExternalDefinitionCreationOptions(
            FamilySeed.WidthParam,
            SpecTypeId.Length)
        {
            GUID = targetGuid
        });
        return targetGuid;
    }

    private void RestoreSharedParameterFile()
    {
        if (_sharedFilePath is null)
        {
            return;
        }

        try
        {
            Application.SharedParametersFilename = _sharedFilePrevious ?? string.Empty;
        }
        catch (Exception)
        {
            // Restoring the shared parameter file is best-effort in the test host.
        }

        try
        {
            if (File.Exists(_sharedFilePath))
            {
                File.Delete(_sharedFilePath);
            }
        }
        catch (IOException)
        {
        }

        _sharedFilePath = null;
    }
}
