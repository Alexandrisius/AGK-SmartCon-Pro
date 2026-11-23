using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// documentId routing of the read tools (night run 2, C9-01/02/08/09): the
/// routing flag of <see cref="AgentReadToolBase"/> agrees with the schemas of
/// the production catalog, the reminder and the routing fact are attached to
/// routed tools only, an undeclared documentId is a typed error instead of a
/// silent read of the active document, and open_model_background states the
/// harness policy of the opened model.
/// </summary>
public sealed class AgentDocumentRoutingTests : RevitApiTest
{
    private const string SeedGroup = "SmartConTests";
    private const string ProjectParameterName = "АДСК_R0_Код";
    private const string ProjectParameterValue = "SC-R0";

    private static readonly string[] SessionToolNames =
    [
        "get_selected_elements", "get_current_view_info", "list_change_sets",
        "get_change_set_details", "list_shared_parameters"
    ];

    private static readonly string[] RoutedSampleNames =
    [
        "list_categories", "list_project_parameters", "get_view_settings",
        "get_family_parameters", "audit_fill_rate", "trace_system_chain",
        "list_views", "query_elements"
    ];

    private Document? _document;
    private List<Document>? _openBefore;
    private List<Document>? _donors;
    private string? _donorPath;
    private string? _tempDirectory;
    private FamilySeed? _seed;
    private SharedParameterFileScope? _sharedFile;
    private RevitThreadEvent? _event;

    private Document Doc => _document!;

    private RevitThreadEvent Event => _event!;

    [Before(Test)]
    public void Seed()
    {
        AgentBackgroundDocuments.ClearForTests();

        // What the host had open before this test: [After(Test)] leaves it
        // untouched and reports only what THIS test leaked.
        _openBefore = Application.Documents.Cast<Document>().ToList();

        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _event = new RevitThreadEvent(Application);
    }

    [After(Test)]
    public void Close()
    {
        AgentBackgroundDocuments.ClearForTests();

        // The donors are closed by THEIR OWN test, never left to the host: an
        // open background document survives into every later test of the
        // session and fails their "only the project document is open"
        // assertions (night run 2 R0: two leaked donors broke the family job
        // tests). Lazy list, no field initializer: a Revit-typed initializer
        // crashes the injector (see #78).
        var donors = _donors;
        _donors = null;
        if (donors is not null)
            foreach (var donor in donors)
                CloseDocument(donor);

        _seed?.Dispose();
        _sharedFile?.Dispose();

        CloseDocument(_document);
        _document = null;

        // Everything else this test left open is closed here too - one leaky
        // test must not break the later tests of the session - and reported,
        // so the leak fails its own test.
        var leaked = new List<string>();
        foreach (var document in Application.Documents.Cast<Document>().ToList())
        {
            if (WasOpenBefore(document))
                continue;

            leaked.Add(document.Title);
            CloseDocument(document);
        }

        // Files only after every document that used them is closed.
        try
        {
            if (!string.IsNullOrEmpty(_tempDirectory) && Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (Exception)
        {
            // Temp directory cleanup is best-effort.
        }

        if (leaked.Count > 0)
        {
            throw new InvalidOperationException(
                "Тест оставил открытыми документы: " + string.Join(", ", leaked));
        }
    }

    [Test]
    public async Task ReadToolCatalog_RoutingFlagMatchesSchema_SessionToolsDoNotRoute()
    {
        using var host = ProductionAgentHost.Create(Doc);

        var readTools = host.Registry.GetAll()
            .OfType<AgentReadToolBase>()
            .ToList();
        await Assert.That(readTools.Count).IsGreaterThanOrEqualTo(43);

        var offenders = new List<string>();
        var routed = new List<string>();
        var session = new List<string>();
        var canonical = CanonicalDocumentIdDescription();
        foreach (var tool in readTools)
        {
            var name = tool.Definition.Name;
            using var schema = JsonDocument.Parse(tool.Definition.InputSchemaJson);
            JsonElement documentId = default;
            var declared = schema.RootElement.TryGetProperty("properties", out var properties)
                           && properties.ValueKind == JsonValueKind.Object
                           && properties.TryGetProperty("documentId", out documentId);

            if (tool.SupportsDocumentRouting != declared)
            {
                offenders.Add(name + ": SupportsDocumentRouting=" + tool.SupportsDocumentRouting +
                              " but schema declares documentId=" + declared);
                continue;
            }

            if (!tool.SupportsDocumentRouting)
            {
                session.Add(name);
                continue;
            }

            routed.Add(name);
            var description = documentId.GetProperty("description").GetString();
            if (!string.Equals(description, canonical, StringComparison.Ordinal))
                offenders.Add(name + ": documentId description differs from the shared one");
        }

        await Assert.That(string.Join("; ", offenders)).IsEqualTo(string.Empty);

        // A stray entry in either list means a tool was reclassified without a
        // decision: session/UI tools never read a background model.
        session.Sort(StringComparer.Ordinal);
        await Assert.That(string.Join(", ", session))
            .IsEqualTo(string.Join(", ", SessionToolNames.OrderBy(name => name, StringComparer.Ordinal)));

        await Assert.That(routed.Count).IsGreaterThanOrEqualTo(38);
        foreach (var sample in RoutedSampleNames)
            await Assert.That(routed.Contains(sample)).IsTrue();
    }

    [Test]
    public async Task BackgroundModelOpen_RoutedReadCarriesFactAndNote_SessionToolsStaySilent()
    {
        EnsureSharedParameterFile();
        var donorId = await OpenDonor();
        var activeId = AgentDocumentIdentity.GetDocumentId(Doc);

        // Routed call WITHOUT documentId: the message states the fact, the
        // payload names the document that was really read.
        var active = await Run(new ListCategoriesTool(new StubRevitContext(Doc)), new { });
        await Assert.That(active.Success).IsTrue();
        await Assert.That(active.Message ?? string.Empty).Contains("documentId was not present in this call");
        await Assert.That(active.Message ?? string.Empty).Contains("repeat with documentId='" + donorId + "'");
        await Assert.That(active.Data!.Value.GetProperty("document").GetProperty("scope").GetString())
            .IsEqualTo("active");
        await Assert.That(active.Data!.Value.GetProperty("document").GetProperty("documentId").GetString())
            .IsEqualTo(activeId);

        // Routed call WITH documentId: no reminder (the model addressed the
        // donor on purpose), the fact says the read ran in the background model.
        var donor = await Run(new ListLevelsTool(new StubRevitContext(Doc)),
            new { documentId = donorId });
        await Assert.That(donor.Success).IsTrue();
        await Assert.That(Lacks(donor.Message, "NOTE: documentId was not present")).IsTrue();
        await Assert.That(donor.Data!.Value.GetProperty("document").GetProperty("scope").GetString())
            .IsEqualTo("background");
        await Assert.That(donor.Data!.Value.GetProperty("document").GetProperty("documentId").GetString())
            .IsEqualTo(donorId);

        // Session tool: no reminder, no routing fact, and a stray documentId
        // (a direct call that bypassed the executor) is never honoured.
        var context = new StubRevitContext(Doc);
        var session = await Run(new ListSharedParametersTool(context), new { });
        await Assert.That(session.Success).IsTrue();
        await Assert.That(Lacks(session.Message, "NOTE: documentId")).IsTrue();
        await Assert.That(session.Data!.Value.TryGetProperty("document", out _)).IsFalse();

        var sessionWithId = await Run(new ListSharedParametersTool(context),
            new { documentId = donorId });
        await Assert.That(sessionWithId.Success).IsTrue();
        await Assert.That(Lacks(sessionWithId.Message, "NOTE: documentId")).IsTrue();
        await Assert.That(sessionWithId.Data!.Value.TryGetProperty("document", out _)).IsFalse();
    }

    [Test]
    public async Task NoBackgroundModel_PlainReadsCarryNothingExtra()
    {
        var result = await Run(new ListCategoriesTool(new StubRevitContext(Doc)), new { });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Lacks(result.Message, "documentId")).IsTrue();
        await Assert.That(result.Data!.Value.TryGetProperty("document", out _)).IsFalse();

        // The active document's own id is an explicit request: only the fact.
        var ownId = AgentDocumentIdentity.GetDocumentId(Doc);
        var explicitActive = await Run(new ListLevelsTool(new StubRevitContext(Doc)),
            new { documentId = ownId });
        await Assert.That(explicitActive.Success).IsTrue();
        await Assert.That(Lacks(explicitActive.Message, "NOTE: documentId")).IsTrue();
        await Assert.That(explicitActive.Data!.Value.GetProperty("document").GetProperty("scope").GetString())
            .IsEqualTo("active");
    }

    [Test]
    public async Task Executor_UndeclaredDocumentId_IsTypedInvalidArguments_ToolNeverRuns()
    {
        using var host = ProductionAgentHost.Create(Doc);

        // Session read tool: the tolerant repair used to delete documentId and
        // the call silently read the active document.
        var read = await host.Executor.ExecuteAsync(
            "get_selected_elements",
            Parse(new { documentId = "any-donor" }),
            CancellationToken.None);
        await Assert.That(read.Success).IsFalse();
        await Assert.That(read.ErrorCode).IsEqualTo("invalid_arguments");
        await Assert.That(read.Message ?? string.Empty)
            .Contains("tool 'get_selected_elements'");
        await Assert.That(read.Message ?? string.Empty)
            .Contains("does not accept documentId");

        // Write tool: the most dangerous variant - a write the model aimed at a
        // donor must not land in the user's active document.
        var write = await host.Executor.ExecuteAsync(
            "rename_views",
            Parse(new
            {
                items = new[] { new { viewId = 123456, newName = "routing-probe" } },
                dryRun = false,
                documentId = "any-donor"
            }),
            CancellationToken.None);
        await Assert.That(write.Success).IsFalse();
        await Assert.That(write.ErrorCode).IsEqualTo("invalid_arguments");
        await Assert.That(write.Message ?? string.Empty).Contains("does not accept documentId");
        await Assert.That(host.ReadAuditEntries()
            .Any(entry => entry.Tool.StartsWith("rename_views", StringComparison.Ordinal)))
            .IsFalse();

        // Root-anyOf write tool (review r1, P2): the routing flag used to skip
        // schemas with a root anyOf, so remove_viewports accepted a stray
        // documentId silently and the write landed in the ACTIVE document.
        var anyOfWrite = await host.Executor.ExecuteAsync(
            "remove_viewports",
            Parse(new
            {
                viewportIds = new[] { 123456 },
                dryRun = true,
                documentId = "any-donor"
            }),
            CancellationToken.None);
        await Assert.That(anyOfWrite.Success).IsFalse();
        await Assert.That(anyOfWrite.ErrorCode).IsEqualTo("invalid_arguments");
        await Assert.That(anyOfWrite.Message ?? string.Empty).Contains("does not accept documentId");
        await Assert.That(host.ReadAuditEntries()
            .Any(entry => entry.Tool.StartsWith("remove_viewports", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    public async Task TwoBackgroundModels_NoteNamesBothDonors_RoutedCallGetsNoNote()
    {
        // Review r1, P3-2: with more than one background model open, the NOTE
        // names every donor's documentId; a call that passes one of them gets
        // no NOTE and reads THAT model.
        var first = await OpenDonor();
        var second = await OpenDonor();

        var plain = await Run(new ListCategoriesTool(new StubRevitContext(Doc)), new { });
        await Assert.That(plain.Success).IsTrue();
        var message = plain.Message ?? string.Empty;
        await Assert.That(message).Contains("2 background models are open");
        await Assert.That(message).Contains("documentId='" + first + "'");
        await Assert.That(message).Contains("documentId='" + second + "'");

        var routed = await Run(new ListLevelsTool(new StubRevitContext(Doc)),
            new { documentId = second });
        await Assert.That(routed.Success).IsTrue();
        await Assert.That(Lacks(routed.Message, "NOTE: documentId")).IsTrue();
        await Assert.That(routed.Data!.Value.GetProperty("document").GetProperty("scope").GetString())
            .IsEqualTo("background");
        await Assert.That(routed.Data!.Value.GetProperty("document").GetProperty("documentId").GetString())
            .IsEqualTo(second);
    }

    [Test]
    public async Task OpenBackground_ReportsHarnessPolicyNextToRevitFlag()
    {
        await OpenDonor();

        var reopened = await Run(new OpenModelBackgroundTool(new StubRevitContext(Doc)),
            new { path = _donorPath });
        await Assert.That(reopened.Success).IsTrue();
        var donorPayload = reopened.Data!.Value;
        await Assert.That(donorPayload.GetProperty("agentPolicy").GetString())
            .IsEqualTo(OpenModelBackgroundTool.AgentDocumentPolicy.ReadOnlyDonor);
        await Assert.That(donorPayload.GetProperty("isActive").GetBoolean()).IsFalse();
        await Assert.That(donorPayload.GetProperty("isReadOnly").GetBoolean()).IsFalse();
        // The user's own document registered under its path keeps the regular
        // write flow: the policy says so, the Revit flag says nothing.
        var mainPath = Path.Combine(
            _tempDirectory!,
            "routing-main-" + Guid.NewGuid().ToString("N") + ".rvt");
        Doc.SaveAs(mainPath);

        var active = await Run(new OpenModelBackgroundTool(new StubRevitContext(Doc)),
            new { path = mainPath });
        await Assert.That(active.Success).IsTrue();
        var activePayload = active.Data!.Value;
        await Assert.That(activePayload.GetProperty("isActive").GetBoolean()).IsTrue();
        await Assert.That(activePayload.GetProperty("agentPolicy").GetString())
            .IsEqualTo(OpenModelBackgroundTool.AgentDocumentPolicy.ActiveDocument);
    }

    [Test]
    public async Task FamilySkillFact_GetFamilyParametersOmitsProjectSharedParameter_ListFamilyTypesShowsIt()
    {
        // Verifies the families skill text (C9-08): get_family_parameters reads
        // the FAMILY document, so a project shared parameter bound to the
        // category is absent there even when the project types carry a value;
        // list_family_types(parameterNames, valueScope:"type") reads it.
        EnsureSharedParameterFile();
        var seed = EnsureFamily();

        var family = Doc.GetElement(new ElementId(seed.FamilyId)) as RevitFamily;
        if (family is null || family.FamilyCategory is null)
            Skip.Test("У сидированного семейства нет категории");

        var file = Application.OpenSharedParameterFile();
        var group = file!.Groups.get_Item(SeedGroup) ?? file.Groups.Create(SeedGroup);
        var definition = (ExternalDefinition)group!.Definitions.Create(
            new ExternalDefinitionCreationOptions(ProjectParameterName, SpecTypeId.String.Text));

        var transactions = new RevitTransactionService(new StubRevitContext(Doc));
        transactions.RunInTransaction(Doc, "Seed project parameter on family types", doc =>
        {
            var categorySet = Application.Create.NewCategorySet();
            categorySet.Insert(family.FamilyCategory);
            var binding = Application.Create.NewTypeBinding(categorySet);
            if (!doc.ParameterBindings.Insert(definition, binding, GroupTypeId.IdentityData))
                throw new InvalidOperationException("Проектный параметр не привязался к категории");

            foreach (var typeId in family.GetFamilySymbolIds())
            {
                if (doc.GetElement(typeId) is FamilySymbol symbol)
                    symbol.get_Parameter(definition)?.Set(ProjectParameterValue);
            }
        });

        var context = new StubRevitContext(Doc);
        var familyParameters = await Run(new GetFamilyParametersTool(context),
            new { familyId = seed.FamilyId, includeValues = true });
        await Assert.That(familyParameters.Success).IsTrue();
        var names = familyParameters.Data!.Value.GetProperty("parameters").EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString() ?? string.Empty)
            .ToList();
        await Assert.That(names.Contains(FamilySeed.WidthParam)).IsTrue();
        await Assert.That(names.Contains(ProjectParameterName)).IsFalse();

        var types = await Run(new ListFamilyTypesTool(context),
            new { familyId = seed.FamilyId, parameterNames = new[] { ProjectParameterName }, valueScope = "type" });
        await Assert.That(types.Success).IsTrue();
        var rows = types.Data!.Value.GetProperty("types").EnumerateArray().ToList();
        var shown = rows.Count(row => row.TryGetProperty("values", out var values)
                                      && values.ValueKind == JsonValueKind.Object
                                      && values.TryGetProperty(ProjectParameterName, out var value)
                                      && value.ValueKind == JsonValueKind.String
                                      && value.GetString() == ProjectParameterValue);
        await Assert.That(rows.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(shown).IsEqualTo(rows.Count);
    }

    [Test]
    public async Task ToolTexts_HeadlessSafeViewMessage_AndAuditCategoriesNarrowOnly()
    {
        // C6-09: no "ask the user to open a view" in a headless session.
        var view = await Run(new GetCurrentViewInfoTool(new StubRevitContext(Doc)), new { });
        await Assert.That(view.ErrorCode).IsEqualTo("no_active_view");
        await Assert.That(view.Message).IsEqualTo(
            "No view is active in this session. Read a view by id with " +
            "get_view_settings(viewId) or list the views with list_views.");

        // C8-05: categories only narrow the audit.
        using var schema = JsonDocument.Parse(new AuditFillRateTool(new StubRevitContext(Doc))
            .Definition.InputSchemaJson);
        var description = schema.RootElement.GetProperty("properties")
            .GetProperty("categories").GetProperty("description").GetString();
        await Assert.That(description ?? string.Empty).Contains("Only narrows the audit");
    }

    // ── Support ────────────────────────────────────────────────────────

    private static void CloseDocument(Document? document)
    {
        try
        {
            if (document is not null && document.IsValidObject)
                document.Close(false);
        }
        catch (Exception)
        {
            // A document that refused to close is reported as leaked in [After(Test)].
        }
    }

    private bool WasOpenBefore(Document document) =>
        _openBefore!.Any(before => ReferenceEquals(before, document)
                                   || AgentDocumentIdentity.IsSameOpenDocument(before, document));

    private static bool Lacks(string? text, string fragment) =>
        (text ?? string.Empty).IndexOf(fragment, StringComparison.Ordinal) < 0;

    private static string CanonicalDocumentIdDescription()
    {
        using var document = JsonDocument.Parse(
            "{" + AgentReadToolBase.DocumentIdPropertyJsonLast + "}");
        return document.RootElement.GetProperty("documentId").GetProperty("description").GetString()!;
    }

    private void EnsureSharedParameterFile() =>
        _sharedFile ??= SharedParameterFileScope.Create(Application);

    private FamilySeed EnsureFamily()
    {
        if (_seed is null)
        {
            _seed = FamilySeed.Create(Application, Doc, "SC_AgentDocRouting", out var reason);
            if (_seed is null)
                Skip.Test(reason ?? "Не удалось сидировать семейство");
        }

        return _seed;
    }

    private async Task<string> OpenDonor()
    {
        _tempDirectory ??= Path.Combine(
            Path.GetTempPath(), "SmartConDocumentRouting", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _donorPath = Path.Combine(_tempDirectory, "donor-" + Guid.NewGuid().ToString("N") + ".rvt");
        var donor = Application.NewProjectDocument(UnitSystem.Metric);
        donor.SaveAs(_donorPath);
        donor.Close(false);

        var open = await Run(new OpenModelBackgroundTool(new StubRevitContext(Doc)),
            new { path = _donorPath });
        if (!open.Success)
            Skip.Test("Не удалось открыть модель-донор: " + open.Message);

        var documentId = open.Data!.Value.GetProperty("documentId").GetString()!;
        if (!AgentBackgroundDocuments.TryResolve(documentId, out var opened))
            Skip.Test("Открытый донор не найден в реестре фоновых моделей: " + open.Message);

        // Handed to [After(Test)]: every donor opened by this test is closed
        // with the test.
        _donors ??= new List<Document>();
        _donors.Add(opened);
        return documentId;
    }

    private async Task<ToolResult> Run(IAgentTool tool, object args)
    {
        var context = new AgentToolContext(
            Event,
            AgentDocumentIdentity.GetDocumentId(Doc),
            Guid.Empty);
        return await tool.ExecuteAsync(context, Parse(args), CancellationToken.None);
    }

    private static JsonElement Parse(object args)
    {
        var json = JsonSerializer.Serialize(args);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
