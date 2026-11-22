using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Microsoft.Data.Sqlite;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Storage;
using SmartCon.Agent.Services.Tools.Read;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 9 v2 multi-model reads (real-cases benchmark + #465): a donor model
/// is opened invisibly by open_model_background, read through the optional
/// documentId argument of the regular read tools, and closed without the
/// donor file or the active document ever being touched. Also covers the
/// model_not_open typed error, the active-document close guard and the
/// list_known_models registry over a real agent.db.
/// </summary>
public sealed class AgentMultiModelTests : RevitApiTest
{
    private const string SeedGroup = "SmartConTests";
    private const string MarkerParameter = "АДСК_Марка";
    private const string MarkerValue = "SC_MM_Эталон";

    private Document? _main;
    private string? _donorPath;
    private SharedParameterFileScope? _sharedFile;
    private RevitThreadEvent? _event;

    private Document Main => _main!;

    private RevitThreadEvent Event => _event!;

    [Before(Test)]
    public void Seed()
    {
        _sharedFile = SharedParameterFileScope.Create(Application);
        _donorPath = SeedDonor();
        AgentBackgroundDocuments.ClearForTests();

        _main = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Main))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _event = new RevitThreadEvent(Application);
    }

    [After(Test)]
    public void Close()
    {
        AgentBackgroundDocuments.ClearForTests();
        _sharedFile?.Dispose();

        try
        {
            _main?.Close(false);
        }
        catch (Exception)
        {
            // The main document may already be closed by a test scenario.
        }

        try
        {
            var directory = Path.GetDirectoryName(_donorPath ?? string.Empty);
            if (!string.IsNullOrEmpty(directory)
                && directory.IndexOf("SmartConMultiModel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Temp directory cleanup is best-effort.
        }
    }

    [Test]
    public async Task OpenBackground_ReadTourThroughDocumentId_CloseLeavesEverythingUntouched()
    {
        var context = new StubRevitContext(Main);
        var identityBefore = AgentDocumentIdentity.GetDocumentId(Main);
        var titleBefore = Main.Title;
        var donorInfoBefore = new FileInfo(_donorPath!);

        // 1. Hidden open of the donor.
        var open = await Run(new OpenModelBackgroundTool(context), new { path = _donorPath });
        await Assert.That(open.Success).IsTrue();
        var openPayload = open.Data!.Value;
        var donorDocumentId = openPayload.GetProperty("documentId").GetString()!;
        await Assert.That(string.IsNullOrEmpty(donorDocumentId)).IsFalse();
        await Assert.That(openPayload.GetProperty("wasAlreadyOpen").GetBoolean()).IsFalse();
        await Assert.That(openPayload.GetProperty("isActive").GetBoolean()).IsFalse();
        await Assert.That(openPayload.GetProperty("isOpen").GetBoolean()).IsTrue();
        await Assert.That(openPayload.TryGetProperty("elementCountPreview", out _)).IsTrue();

        // The active document did not change (case 9 core invariant).
        await Assert.That(ReferenceEquals(context.TryGetDocument(), Main)).IsTrue();
        await Assert.That(Main.Title).IsEqualTo(titleBefore);
        await Assert.That(AgentDocumentIdentity.GetDocumentId(Main)).IsEqualTo(identityBefore);
        await Assert.That(AgentBackgroundDocuments.ListAll().Count).IsEqualTo(1);

        // 2. Read tour: donor views through documentId routing.
        var donorViews = await Run(new ListViewsTool(context),
            new { nameContains = "SC_MM_Эталон", documentId = donorDocumentId });
        await Assert.That(donorViews.Success).IsTrue();
        var donorViewNames = donorViews.Data!.Value.GetProperty("views").EnumerateArray()
            .Select(view => view.GetProperty("name").GetString())
            .ToList();
        await Assert.That(donorViewNames.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(donorViewNames.Any(name => name == "SC_MM_Эталон 1_План")).IsTrue();
        await Assert.That(donorViewNames.Any(name => name == "SC_MM_Эталон 2_План")).IsTrue();

        // The donor levels are visible through routing as well...
        var donorLevels = await Run(new ListLevelsTool(context),
            new { documentId = donorDocumentId });
        await Assert.That(donorLevels.Success).IsTrue();
        var donorLevelNames = donorLevels.Data!.Value.GetProperty("levels").EnumerateArray()
            .Select(level => level.GetProperty("name").GetString())
            .ToList();
        await Assert.That(donorLevelNames.Any(name => name == "SC_MM_Этаж 1")).IsTrue();
        await Assert.That(donorLevelNames.Any(name => name == "SC_MM_Этаж 2")).IsTrue();

        // ...while the same tools without documentId still read the active model.
        var activeLevels = await Run(new ListLevelsTool(context), new { });
        await Assert.That(activeLevels.Success).IsTrue();
        var activeLevelNames = activeLevels.Data!.Value.GetProperty("levels").EnumerateArray()
            .Select(level => level.GetProperty("name").GetString())
            .ToList();
        await Assert.That(activeLevelNames.Any(name => name!.Contains("SC_MM_"))).IsFalse();

        // 3. Close: registry empty, donor file untouched (no writes to the donor).
        var close = await Run(new CloseModelTool(context),
            new { documentId = donorDocumentId });
        await Assert.That(close.Success).IsTrue();
        await Assert.That(close.Data!.Value.GetProperty("saved").GetBoolean()).IsFalse();
        await Assert.That(AgentBackgroundDocuments.ListAll().Count).IsEqualTo(0);

        var donorInfoAfter = new FileInfo(_donorPath!);
        await Assert.That(donorInfoAfter.Length).IsEqualTo(donorInfoBefore.Length);
        await Assert.That(donorInfoAfter.LastWriteTimeUtc)
            .IsEqualTo(donorInfoBefore.LastWriteTimeUtc);

        // The active session survived the whole cycle.
        await Assert.That(AgentDocumentIdentity.GetDocumentId(Main)).IsEqualTo(identityBefore);

        // Repeated close is an honest model_not_open, not a crash.
        var closedAgain = await Run(new CloseModelTool(context),
            new { documentId = donorDocumentId });
        await Assert.That(closedAgain.Success).IsFalse();
        await Assert.That(closedAgain.ErrorCode).IsEqualTo("model_not_open");
    }

    [Test]
    public async Task ReadTool_UnknownDocumentId_FailsTypedWithHint()
    {
        var context = new StubRevitContext(Main);
        var unknownId = "C:\\missing\\donor.rvt|00000000-0000-0000-0000-000000000000";

        var views = await Run(new ListViewsTool(context),
            new { documentId = unknownId });
        await Assert.That(views.Success).IsFalse();
        await Assert.That(views.ErrorCode).IsEqualTo("model_not_open");
        await Assert.That(views.Message ?? string.Empty).Contains("open_model_background");

        var levels = await Run(new ListLevelsTool(context),
            new { documentId = unknownId });
        await Assert.That(levels.Success).IsFalse();
        await Assert.That(levels.ErrorCode).IsEqualTo("model_not_open");

        // The active document's own id routes to the active document (no error).
        var ownId = AgentDocumentIdentity.GetDocumentId(Main);
        var active = await Run(new ListLevelsTool(context), new { documentId = ownId });
        await Assert.That(active.Success).IsTrue();
    }

    [Test]
    public async Task OpenBackground_AlreadyOpenActive_RegistersIt_AndCloseRefuses()
    {
        var mainPath = Path.Combine(
            Path.GetDirectoryName(_donorPath!)!,
            "main-" + Guid.NewGuid().ToString("N") + ".rvt");
        Main.SaveAs(mainPath);

        var context = new StubRevitContext(Main);

        var open = await Run(new OpenModelBackgroundTool(context), new { path = mainPath });
        await Assert.That(open.Success).IsTrue();
        var payload = open.Data!.Value;
        await Assert.That(payload.GetProperty("wasAlreadyOpen").GetBoolean()).IsTrue();
        await Assert.That(payload.GetProperty("isActive").GetBoolean()).IsTrue();
        await Assert.That(open.Warnings.Count).IsGreaterThanOrEqualTo(1);

        var close = await Run(new CloseModelTool(context),
            new { documentId = payload.GetProperty("documentId").GetString()! });
        await Assert.That(close.Success).IsFalse();
        await Assert.That(close.ErrorCode).IsEqualTo("model_is_active");

        // The main document is still open and usable after the refusal.
        var levels = await Run(new ListLevelsTool(context), new { });
        await Assert.That(levels.Success).IsTrue();
    }

    [Test]
    public async Task OpenBackground_MissingPath_FailsTyped()
    {
        var context = new StubRevitContext(Main);
        var missing = Path.Combine(
            Path.GetDirectoryName(_donorPath!)!,
            "does-not-exist-" + Guid.NewGuid().ToString("N") + ".rvt");

        var open = await Run(new OpenModelBackgroundTool(context), new { path = missing });
        await Assert.That(open.Success).IsFalse();
        await Assert.That(open.ErrorCode).IsEqualTo("file_not_found");

        var relative = await Run(new OpenModelBackgroundTool(context),
            new { path = "donor.rvt" });
        await Assert.That(relative.Success).IsFalse();
        await Assert.That(relative.ErrorCode).IsEqualTo("invalid_arguments");
    }

    [Test]
    public async Task ListKnownModels_AggregatesSessionsChangeSetsAndJobs()
    {
        using var fixture = new DatabaseFixture();
        fixture.Database.Initialize();

        var pathA = "C:\\Models\\КорпусА.rvt";
        var pathB = "C:\\Models\\Открытый.rvt";
        SeedRegistryRows(fixture.Database, pathA, pathB);

        var tool = new ListKnownModelsTool(fixture.Database);

        var all = await tool.ExecuteAsync(
            new AgentToolContext(Event, "test-document", Guid.Empty),
            Parse(new { }), CancellationToken.None);
        await Assert.That(all.Success).IsTrue();

        var models = all.Data!.Value.GetProperty("models").EnumerateArray().ToList();
        await Assert.That(models.Count).IsEqualTo(2);

        var modelA = models.First(model =>
            string.Equals(model.GetProperty("path").GetString(), pathA, StringComparison.Ordinal));
        await Assert.That(modelA.GetProperty("sessions").GetInt32()).IsEqualTo(2);
        await Assert.That(modelA.GetProperty("changeSets").GetInt32()).IsEqualTo(1);
        await Assert.That(modelA.GetProperty("jobs").GetInt32()).IsEqualTo(1);
        await Assert.That(modelA.GetProperty("lastChange").GetProperty("operation").GetString())
            .IsEqualTo("rename_views");
        await Assert.That(modelA.TryGetProperty("currentlyOpen", out _)).IsTrue();

        // «В какой модели делали Т1» — search by the change summary.
        var search = await tool.ExecuteAsync(
            new AgentToolContext(Event, "test-document", Guid.Empty),
            Parse(new { query = "Т1" }), CancellationToken.None);
        await Assert.That(search.Success).IsTrue();
        var searchPaths = search.Data!.Value.GetProperty("models").EnumerateArray()
            .Select(model => model.GetProperty("path").GetString())
            .ToList();
        await Assert.That(searchPaths.Count).IsEqualTo(1);
        await Assert.That(string.Equals(searchPaths[0], pathA, StringComparison.Ordinal)).IsTrue();

        // B was never touched by change sets or jobs — sessions only.
        var modelB = models.First(model =>
            string.Equals(model.GetProperty("path").GetString(), pathB, StringComparison.Ordinal));
        await Assert.That(modelB.GetProperty("sessions").GetInt32()).IsEqualTo(1);
        await Assert.That(modelB.GetProperty("changeSets").GetInt32()).IsEqualTo(0);
        await Assert.That(modelB.GetProperty("jobs").GetInt32()).IsEqualTo(0);
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    private string SeedDonor()
    {
        var donor = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(donor))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        var directory = Path.Combine(Path.GetTempPath(), "SmartConMultiModel");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor-" + Guid.NewGuid().ToString("N") + ".rvt");

        var transactions = new RevitTransactionService(new StubRevitContext(donor));
        transactions.RunInTransaction(donor, "Seed donor model", doc =>
        {
            var level1 = ModelSeed.CreateLevel(doc, 0.0);
            var level2 = ModelSeed.CreateLevel(doc, 3.0);
            level1.Name = "SC_MM_Этаж 1";
            level2.Name = "SC_MM_Этаж 2";
            _ = ModelSeed.CreateViewPlanChecked(doc, level1.Id, "SC_MM_Эталон 1_План");
            _ = ModelSeed.CreateViewPlanChecked(doc, level2.Id, "SC_MM_Эталон 2_План");

            var wall = ModelSeed.TryCreateWall(doc, level1.Id);
            if (wall is null)
                return;

            var definition = SeedMarkerDefinition();
            var walls = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Walls);
            var categorySet = Application.Create.NewCategorySet();
            categorySet.Insert(walls);
            var binding = Application.Create.NewTypeBinding(categorySet);
            _ = doc.ParameterBindings.Insert(definition, binding, GroupTypeId.IdentityData);

            var wallType = doc.GetElement(wall.GetTypeId()) as ElementType;
            wallType?.get_Parameter(definition)?.Set(MarkerValue);
        });

        donor.SaveAs(donorPath);
        donor.Close(false);
        return donorPath;
    }

    private ExternalDefinition SeedMarkerDefinition()
    {
        var file = Application.OpenSharedParameterFile();
        var group = file!.Groups.get_Item(SeedGroup) ?? file.Groups.Create(SeedGroup);
        return (ExternalDefinition)group!.Definitions.Create(
            new ExternalDefinitionCreationOptions(MarkerParameter, SpecTypeId.String.Text));
    }

    private static void SeedRegistryRows(AgentDatabase database, string pathA, string pathB)
    {
        using var connection = database.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, @"
INSERT INTO sessions(id, document_id, title, document_path, revit_version, created_at, updated_at, token_count)
VALUES(@id, @documentId, @title, @path, '2026', @createdAt, @updatedAt, 100);",
            ("@id", "s1"), ("@documentId", "docA"), ("@title", "Корпус А"),
            ("@path", pathA),
            ("@createdAt", "2026-01-01T10:00:00.0000000Z"),
            ("@updatedAt", "2026-01-01T10:05:00.0000000Z"));
        Execute(connection, transaction, @"
INSERT INTO sessions(id, document_id, title, document_path, revit_version, created_at, updated_at, token_count)
VALUES(@id, @documentId, @title, @path, '2026', @createdAt, @updatedAt, 100);",
            ("@id", "s2"), ("@documentId", "docA"), ("@title", "Корпус А"),
            ("@path", pathA),
            ("@createdAt", "2026-01-02T10:00:00.0000000Z"),
            ("@updatedAt", "2026-01-02T10:05:00.0000000Z"));
        Execute(connection, transaction, @"
INSERT INTO sessions(id, document_id, title, document_path, revit_version, created_at, updated_at, token_count)
VALUES(@id, @documentId, @title, @path, '2026', @createdAt, @updatedAt, 100);",
            ("@id", "s3"), ("@documentId", "docB"), ("@title", "Открытый"),
            ("@path", pathB),
            ("@createdAt", "2026-01-03T10:00:00.0000000Z"),
            ("@updatedAt", "2026-01-03T10:05:00.0000000Z"));
        Execute(connection, transaction, @"
INSERT INTO change_sets(id, session_id, tool_call_id, operation, document_id, document_path,
                        revit_version, author, description, created_at, applied_at)
VALUES(@id, 's2', NULL, 'rename_views', 'docA', @path, '2026', 'agent', @description,
       @createdAt, @createdAt);",
            ("@id", "c1"),
            ("@path", pathA),
            ("@description", "настроили Т1: переименовали виды отопления"),
            ("@createdAt", "2026-01-02T10:04:00.0000000Z"));
        Execute(connection, transaction, @"
INSERT INTO jobs(id, document_id, action, mode, spec_json, state, created_at, updated_at)
VALUES('j1', 'docA', 'rename_views', 0, '{}', 4,
       '2026-01-02T10:10:00.0000000Z', '2026-01-02T10:11:00.0000000Z');");

        transaction.Commit();
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<ToolResult> Run(IAgentTool tool, object args)
    {
        var context = new AgentToolContext(
            Event,
            AgentDocumentIdentity.GetDocumentId(Main),
            Guid.Empty);
        return await tool.ExecuteAsync(context, Parse(args), CancellationToken.None);
    }

    private static JsonElement Parse(object args)
    {
        var json = JsonSerializer.Serialize(args);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class RevitThreadEvent : IAgentAwaitableEvent
    {
        private readonly Application _application;

        public RevitThreadEvent(Application application)
        {
            _application = application;
        }

        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default) =>
            Task.FromResult(funcWithApp(_application));

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            actionWithApp(_application);
            return Task.CompletedTask;
        }

        public void ProcessQueue(object revitApp)
        {
        }

        public void Initialize(Action onRaise)
        {
        }
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "smartcon-agent-multimodel-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Database = new AgentDatabase(Path.Combine(_directory, "agent.db"));
        }

        public AgentDatabase Database { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (Exception)
            {
                // Temp directory cleanup is best-effort.
            }
        }
    }
}
