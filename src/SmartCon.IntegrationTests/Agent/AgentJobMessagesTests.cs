using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Storage;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Jobs;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Message honesty of the family/job tools against a real document: a family name
/// that matched nothing names the nearest loaded families (C2-07), and
/// find_families_with_parameter never reports an unscanned instance count as zero
/// (C7-08).
/// </summary>
public sealed class AgentJobMessagesTests : RevitApiTest
{
    private const string FamilyPrefix = "SC_JobMsg_";

    private Document? _document;
    private readonly List<FamilySeed> _seeds = new();

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
    }

    [After(Test)]
    public void Cleanup()
    {
        foreach (var seed in _seeds)
        {
            seed.Dispose();
        }

        _seeds.Clear();
        Doc.Close(false);
    }

    /// <summary>
    /// A typo in select.familyNames warns with the nearest loaded name; the exact
    /// name in the same selector still resolves.
    /// </summary>
    [Test]
    public async Task FamilySelector_UnknownName_WarnsWithNearestLoadedName()
    {
        var seed = FamilySeed.Create(Application, Doc, FamilyPrefix + "1", out var reason);
        if (seed is null)
        {
            Skip.Test("Family template or family document creation is unavailable: " + reason);
        }

        _seeds.Add(seed!);

        var typo = FamilyPrefix + "l";
        var spec =
            "{\"select\":{\"familyNames\":[\"" + typo + "\",\"" + FamilyPrefix +
            "1\"],\"limit\":1},\"mappings\":[" +
            "{\"source\":{\"name\":\"" + FamilySeed.WidthParam + "\"}," +
            "\"target\":{\"name\":\"" + FamilySeed.HeightParam + "\"}}]}";

        var databasePath = Path.Combine(
            Path.GetTempPath(), "sc-jobmsg-" + Guid.NewGuid().ToString("N") + ".db");
        var database = new AgentDatabase(databasePath);
        database.Initialize();
        var store = new SqliteAgentJobStore(database);
        var runner = new JobRunner(
            store,
            new IJobAction[] { new MapFamilyParametersJobAction() },
            new RevitTransactionService(new StubRevitContext(Doc)));

        var start = runner.Start(
            new JobSpec
            {
                DocumentId = AgentDocumentIdentity.GetDocumentId(Doc),
                Action = "map_family_parameters",
                Mode = JobMode.Preview,
                SpecJson = spec,
                Options = new JobOptions { SavePolicy = JobSavePolicy.Never, BatchSize = 1, SliceBudgetMs = 1500 }
            },
            Doc);
        runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1));

        // The selector decides before the family is opened: nothing may depend on
        // the editability of the seeded family, so both outcomes carry the hint.
        var record = await store.GetAsync(start.Job?.JobId ?? Guid.Empty, CancellationToken.None);
        var warnings = record?.PreviewJson is null
            ? new List<string>()
            : (JobJson.Deserialize<JobPreview>(record.PreviewJson)?.Warnings ?? new List<string>());

        var texts = new List<string> { start.Error?.ToMessage() ?? string.Empty };
        texts.AddRange(warnings);
        var joined = string.Join("\n", texts);
        await Assert.That(joined.Contains("No loaded family named '" + typo + "'.", StringComparison.Ordinal)).IsTrue();
        await Assert.That(joined.Contains("Nearest loaded name(s): " + FamilyPrefix + "1.", StringComparison.Ordinal)).IsTrue();

        try
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Without includeInstanceParameters the instances are not scanned: the count
    /// is null with instancesScanned=false, never a misleading zero.
    /// </summary>
    [Test]
    public async Task FindFamilies_UnscannedInstances_NullCountNotZero()
    {
        var seed = FamilySeed.Create(Application, Doc, FamilyPrefix + "1", out var reason);
        if (seed is null)
        {
            Skip.Test("Family template or family document creation is unavailable: " + reason);
        }

        _seeds.Add(seed!);

        var tool = new FindFamiliesWithParameterTool(new StubRevitContext(Doc));

        var unscanned = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"" + FamilySeed.WidthParam + "\"}"),
            CancellationToken.None);
        await Assert.That(unscanned.Success).IsTrue();
        var row = unscanned.Data!.Value.GetProperty("families")[0];
        await Assert.That(row.GetProperty("placedInstanceCount").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        await Assert.That(row.GetProperty("instancesScanned").GetBoolean()).IsFalse();

        var scanned = tool.ExecuteForTests(
            Doc,
            Parse("{\"parameterName\":\"" + FamilySeed.WidthParam + "\",\"includeInstanceParameters\":true}"),
            CancellationToken.None);
        await Assert.That(scanned.Success).IsTrue();
        row = scanned.Data!.Value.GetProperty("families")[0];
        await Assert.That(row.GetProperty("placedInstanceCount").GetInt32()).IsEqualTo(0);
        await Assert.That(row.GetProperty("instancesScanned").GetBoolean()).IsTrue();
    }

    /// <summary>
    /// A preview that does not settle inside start_job (Revit is busy, so the
    /// pump runs late) is reported as still running with the way to follow it,
    /// never as a finished preview.
    /// </summary>
    [Test]
    public async Task StartJob_PreviewNotSettledInTime_SaysStillRunning()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), "sc-jobmsg-" + Guid.NewGuid().ToString("N") + ".db");
        var database = new AgentDatabase(databasePath);
        database.Initialize();
        var store = new SqliteAgentJobStore(database);
        var documentId = AgentDocumentIdentity.GetDocumentId(Doc);
        var runner = new JobRunner(
            store,
            new IJobAction[] { new RefusingJobAction { UnitCount = 4, RefuseAtSeq = 99 } },
            new RevitTransactionService(new StubRevitContext(Doc)));

        // A busy Revit UI: the pump is raised, but no slice runs until the UI is idle again.
        runner.Initialize(static () => { });

        var context = new AgentToolContext(new InlineAwaitableEvent(), documentId, Guid.Empty);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var start = await new StartJobTool(runner, store, new StubRevitContext(Doc)).ExecuteAsync(
            context,
            Parse("{\"action\":\"refusing_probe\",\"dryRun\":true}"),
            CancellationToken.None);
        watch.Stop();

        await Assert.That(start.Success).IsTrue();
        await Assert.That(start.Message).IsEqualTo(
            "Preview still running after 8 s. Call get_job_status (waitSeconds=10) to follow progress.");
        var data = start.Data!.Value;
        await Assert.That(data.GetProperty("state").GetString()).IsEqualTo("PreviewRunning");
        await Assert.That(data.GetProperty("mustVerify").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("next").GetString()).IsEqualTo(
            "wait: call get_job_status (waitSeconds=10) until state=PreviewReady");
        await Assert.That(watch.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(8));

        // The advice leads to the settled preview once the pump runs again.
        runner.Initialize(() => runner.RunToCompletion(Doc, TimeSpan.FromMinutes(1)));
        var status = await new GetJobStatusTool(store, runner).ExecuteAsync(
            context,
            Parse("{\"waitSeconds\":0}"),
            CancellationToken.None);
        await Assert.That(status.Success).IsTrue();
        await Assert.That(status.Message!.StartsWith("PreviewReady", StringComparison.Ordinal)).IsTrue();

        try
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
        catch (IOException)
        {
        }
    }

    private static System.Text.Json.JsonElement Parse(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class InlineAwaitableEvent : IAgentAwaitableEvent
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
}
