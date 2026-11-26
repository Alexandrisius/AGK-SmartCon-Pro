using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// G.3 project shared parameters: list_project_parameters reads the document
/// bindings and add_project_shared_parameter binds a ФОП definition to project
/// categories (dry-run, createDefinition, extension, undo, typed failures).
/// The shared parameter file is swapped to a temp file, never the user's one.
/// </summary>
public sealed class AgentProjectSharedParametersTests : RevitApiTest
{
    private const string SeedGroup = "SmartConTests";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private SharedFileScope? _sharedFile;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _sharedFile = SharedFileScope.Create(Application);
    }

    [After(Test)]
    public void Close()
    {
        _sharedFile?.Dispose();
        Doc.Close(false);
    }

    [Test]
    public async Task List_EmptyForFreshNamespace_AndSeesBindingAfterApply()
    {
        var empty = List("{\"parameterNameContains\":\"SC_Proj_\"}");
        await Assert.That(empty.Success).IsTrue();
        var emptyData = Data(empty);
        await Assert.That(emptyData.GetProperty("total").GetInt32()).IsEqualTo(0);
        await Assert.That(emptyData.GetProperty("parameters").GetArrayLength()).IsEqualTo(0);

        SeedDefinition("SC_Proj_ListProbe", SpecTypeId.String.Text);
        var add = Add(
            "{\"name\":\"SC_Proj_ListProbe\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"binding\":\"type\",\"dryRun\":false}");
        await Assert.That(add.Success).IsTrue();

        var listed = List("{\"parameterNameContains\":\"SC_Proj_ListProbe\"}");
        await Assert.That(listed.Success).IsTrue();
        var row = SingleRow(listed);
        await Assert.That(row.GetProperty("name").GetString()).IsEqualTo("SC_Proj_ListProbe");
        await Assert.That(row.GetProperty("isShared").GetBoolean()).IsTrue();
        await Assert.That(row.GetProperty("bindingType").GetString()).IsEqualTo("type");
        await Assert.That(string.IsNullOrEmpty(row.GetProperty("guid").GetString())).IsFalse();
        var categories = row.GetProperty("categories").EnumerateArray()
            .Select(category => category.GetString()!)
            .ToList();
        await Assert.That(categories)
            .Contains(Category.GetCategory(Doc, BuiltInCategory.OST_PipeCurves)!.Name);
    }

    [Test]
    public async Task List_MessageSeparatesMatchedProjectAndPage()
    {
        // #535: "Read 136 of 136" hid the three different numbers (matched,
        // whole project, page cut). The payload/message must carry all of them.
        SeedDefinition("SC_Proj_ListA", SpecTypeId.String.Text);
        SeedDefinition("SC_Proj_ListB", SpecTypeId.String.Text);
        var first = Add(
            "{\"name\":\"SC_Proj_ListA\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"binding\":\"type\",\"dryRun\":false}");
        var second = Add(
            "{\"name\":\"SC_Proj_ListB\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"binding\":\"type\",\"dryRun\":false}");
        await Assert.That(first.Success).IsTrue();
        await Assert.That(second.Success).IsTrue();

        var filtered = List("{\"parameterNameContains\":\"SC_Proj_ListA\"}");
        await Assert.That(filtered.Success).IsTrue();
        var data = Data(filtered);
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("filtered").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("totalInProject").GetInt32())
            .IsGreaterThanOrEqualTo(2);
        await Assert.That(filtered.Message!).Contains("matching binding(s)");
        await Assert.That(filtered.Message!).Contains("parameter definition(s) bound in the project");

        var paged = List("{\"parameterNameContains\":\"SC_Proj_List\",\"limit\":1}");
        await Assert.That(paged.Success).IsTrue();
        var pageData = Data(paged);
        await Assert.That(pageData.GetProperty("total").GetInt32()).IsEqualTo(2);
        await Assert.That(pageData.GetProperty("returned").GetInt32()).IsEqualTo(1);
        await Assert.That(pageData.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(paged.Message!).Contains("more with offset");
    }

    [Test]
    public async Task Rebind_SwitchesBindingType_WithValuesLostWarning()
    {
        SeedDefinition("SC_Proj_Rebind", SpecTypeId.String.Text);
        var add = Add(
            "{\"name\":\"SC_Proj_Rebind\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"binding\":\"instance\",\"dryRun\":false}");
        await Assert.That(add.Success).IsTrue();

        var tool = new RebindProjectParameterTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);

        var dryRun = tool.ExecuteForTests(
            Parse("{\"name\":\"SC_Proj_Rebind\",\"binding\":\"type\",\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(dryRun.Success).IsTrue();
        // The dry run of the switch reports the blast radius and the value loss.
        await Assert.That(Data(dryRun).GetProperty("valuesWillBeLost").GetBoolean()).IsTrue();
        await Assert.That(Data(dryRun).GetProperty("targetBinding").GetString()).IsEqualTo("type");

        // The same-binding request is an honest skip.
        var skip = tool.ExecuteForTests(
            Parse("{\"name\":\"SC_Proj_Rebind\",\"binding\":\"instance\",\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(skip.Success).IsTrue();
        await Assert.That(skip.Message!).Contains("already");

        var applied = tool.ExecuteForTests(
            Parse("{\"name\":\"SC_Proj_Rebind\",\"binding\":\"type\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();
        await Assert.That(applied.Message!).Contains("type");

        var row = SingleRow(List("{\"parameterNameContains\":\"SC_Proj_Rebind\"}"));
        await Assert.That(row.GetProperty("bindingType").GetString()).IsEqualTo("type");

        var missing = tool.ExecuteForTests(
            Parse("{\"name\":\"SC_Proj_NoSuch\",\"binding\":\"type\",\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(missing.Success).IsFalse();
        await Assert.That(missing.ErrorCode).IsEqualTo("parameter_not_found");
    }

    [Test]
    public async Task Add_DryRun_ChangesNothing()
    {
        SeedDefinition("SC_Proj_DryRun", SpecTypeId.String.Text);

        var result = Add(
            "{\"name\":\"SC_Proj_DryRun\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"dryRun\":true}");

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.TryGetProperty("changeSetId", out _)).IsFalse();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);

        var listed = List("{\"parameterNameContains\":\"SC_Proj_DryRun\"}");
        await Assert.That(Data(listed).GetProperty("total").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task Add_ApplyWithExistingDefinition_BindsAndReadsBack()
    {
        SeedDefinition("SC_Proj_Apply", SpecTypeId.String.Text);

        var result = Add(
            "{\"name\":\"SC_Proj_Apply\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\",\"OST_Walls\"],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(string.IsNullOrEmpty(
                data.GetProperty("changeSetId").GetString()))
            .IsFalse();
        var parameter = data.GetProperty("parameter");
        await Assert.That(string.IsNullOrEmpty(parameter.GetProperty("guid").GetString()))
            .IsFalse();
        await Assert.That(data.GetProperty("readBack").GetProperty("found").GetBoolean())
            .IsTrue();
        await Assert.That(data.GetProperty("readBack").GetProperty("bindingType").GetString())
            .IsEqualTo("instance");
        await Assert.That(data.TryGetProperty("definitionCreated", out _)).IsFalse();

        var row = SingleRow(List("{\"parameterNameContains\":\"SC_Proj_Apply\"}"));
        await Assert.That(row.GetProperty("categories").GetArrayLength()).IsEqualTo(2);

        var changeSet = _changeSets!.Last;
        await Assert.That(changeSet).IsNotNull();
        await Assert.That(changeSet!.Header.Operation).IsEqualTo("add_project_shared_parameter");
        await Assert.That(changeSet.Items[0].ParameterName).IsEqualTo("project_parameter_binding");
        await Assert.That(changeSet.Items[0].OldValue).IsNull();

        var pipeName = Category.GetCategory(Doc, BuiltInCategory.OST_PipeCurves)!.Name;
        var listed = List(
            "{\"categoryContains\":\"" + pipeName.Substring(0, Math.Min(3, pipeName.Length)) +
            "\"}");
        await Assert.That(Data(listed).GetProperty("total").GetInt32()).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Add_DefinitionMissingWithoutCreateDefinition_FailsTyped()
    {
        var result = Add(
            "{\"name\":\"SC_Proj_NeverSeeded\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("definition_not_found");

        var listed = List("{\"parameterNameContains\":\"SC_Proj_NeverSeeded\"}");
        await Assert.That(Data(listed).GetProperty("total").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task Add_CreateDefinition_CreatesInFileGroupAndBinds()
    {
        var result = Add(
            "{\"name\":\"SC_Proj_Created\",\"spec\":\"length\",\"group\":\"SmartCon\"," +
            "\"createDefinition\":true,\"categories\":[\"OST_PipeCurves\"],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("definitionCreated").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("note").GetString())
            .IsEqualTo("the definition stays in the shared parameter file");
        await Assert.That(data.GetProperty("parameter").GetProperty("definitionGroup").GetString())
            .IsEqualTo("SmartCon");

        var file = Doc.Application.OpenSharedParameterFile();
        var found = false;
        foreach (DefinitionGroup group in file!.Groups)
        {
            if (!string.Equals(group.Name, "SmartCon", StringComparison.Ordinal))
                continue;
            foreach (Definition definition in group.Definitions)
            {
                if (string.Equals(definition.Name, "SC_Proj_Created", StringComparison.Ordinal))
                    found = true;
            }
        }

        await Assert.That(found).IsTrue();

        var row = SingleRow(List("{\"parameterNameContains\":\"SC_Proj_Created\"}"));
        await Assert.That(row.GetProperty("guid").GetString()).IsNotNull();
    }

    [Test]
    public async Task Add_SecondCallWithNewCategory_ExtendsBinding()
    {
        SeedDefinition("SC_Proj_Extend", SpecTypeId.String.Text);

        var first = Add(
            "{\"name\":\"SC_Proj_Extend\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"dryRun\":false}");
        await Assert.That(first.Success).IsTrue();

        var second = Add(
            "{\"name\":\"SC_Proj_Extend\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_Walls\"],\"dryRun\":false}");
        await Assert.That(second.Success).IsTrue();

        var row = SingleRow(List("{\"parameterNameContains\":\"SC_Proj_Extend\"}"));
        var categories = row.GetProperty("categories").EnumerateArray()
            .Select(category => category.GetString()!)
            .ToList();
        await Assert.That(categories.Count).IsEqualTo(2);
        await Assert.That(categories)
            .Contains(Category.GetCategory(Doc, BuiltInCategory.OST_PipeCurves)!.Name);
        await Assert.That(categories)
            .Contains(Category.GetCategory(Doc, BuiltInCategory.OST_Walls)!.Name);

        var third = Add(
            "{\"name\":\"SC_Proj_Extend\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"dryRun\":false}");
        await Assert.That(third.Success).IsTrue();
        await Assert.That(Data(third).GetProperty("errorCode").GetString()).IsEqualTo("no_changes");
    }

    [Test]
    public async Task Undo_AfterApply_RemovesBinding()
    {
        SeedDefinition("SC_Proj_UndoRemove", SpecTypeId.String.Text);
        var add = Add(
            "{\"name\":\"SC_Proj_UndoRemove\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"dryRun\":false}");
        await Assert.That(add.Success).IsTrue();

        var undo = Undo();
        await Assert.That(undo.Success).IsTrue();

        var listed = List("{\"parameterNameContains\":\"SC_Proj_UndoRemove\"}");
        await Assert.That(Data(listed).GetProperty("total").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task Undo_AfterExtend_RestoresPreviousCategorySet()
    {
        SeedDefinition("SC_Proj_UndoExtend", SpecTypeId.String.Text);
        var first = Add(
            "{\"name\":\"SC_Proj_UndoExtend\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"dryRun\":false}");
        await Assert.That(first.Success).IsTrue();
        var second = Add(
            "{\"name\":\"SC_Proj_UndoExtend\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_Walls\"],\"dryRun\":false}");
        await Assert.That(second.Success).IsTrue();

        var undo = Undo();
        await Assert.That(undo.Success).IsTrue();

        var row = SingleRow(List("{\"parameterNameContains\":\"SC_Proj_UndoExtend\"}"));
        var categories = row.GetProperty("categories").EnumerateArray()
            .Select(category => category.GetString()!)
            .ToList();
        await Assert.That(categories.Count).IsEqualTo(1);
        await Assert.That(categories)
            .Contains(Category.GetCategory(Doc, BuiltInCategory.OST_PipeCurves)!.Name);
    }

    [Test]
    public async Task Add_UnknownCategory_FailsTypedPerItem()
    {
        var result = Add(
            "{\"name\":\"SC_Proj_BadCategory\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_NoSuchCategory\"],\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        var data = Data(result);
        await Assert.That(data.GetProperty("failed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("failed")[0].GetProperty("errorCode").GetString())
            .IsEqualTo("category_not_found");
    }

    [Test]
    public async Task Add_BindingTypeMismatch_FailsTyped()
    {
        SeedDefinition("SC_Proj_TypeConflict", SpecTypeId.String.Text);
        var instance = Add(
            "{\"name\":\"SC_Proj_TypeConflict\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"binding\":\"instance\",\"dryRun\":false}");
        await Assert.That(instance.Success).IsTrue();

        var typeCall = Add(
            "{\"name\":\"SC_Proj_TypeConflict\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_Walls\"],\"binding\":\"type\",\"dryRun\":false}");

        await Assert.That(typeCall.Success).IsFalse();
        await Assert.That(typeCall.ErrorCode).IsEqualTo("binding_type_conflict");
    }

    [Test]
    public async Task Remove_DryRun_ThenApply_ThenDefinitionGate()
    {
        SeedDefinition("SC_Proj_Remove", SpecTypeId.String.Text);
        var add = Add(
            "{\"name\":\"SC_Proj_Remove\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"binding\":\"instance\",\"dryRun\":false}");
        await Assert.That(add.Success).IsTrue();
        var guid = Guid.Parse(SingleRow(List("{\"parameterNameContains\":\"SC_Proj_Remove\"}"))
            .GetProperty("guid").GetString()!);

        var tool = new RemoveProjectParameterTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);

        // Dry run: audit only — the binding stays, the loss is stated.
        var dryRun = tool.ExecuteForTests(
            Parse("{\"name\":\"SC_Proj_Remove\",\"dryRun\":true}"),
            approved: true, CancellationToken.None);
        await Assert.That(dryRun.Success).IsTrue();
        await Assert.That(Data(dryRun).GetProperty("valuesWillBeLost").GetBoolean()).IsTrue();
        await Assert.That(Data(dryRun).GetProperty("undoSupported").GetBoolean()).IsFalse();
        await Assert.That(SingleRow(List("{\"parameterNameContains\":\"SC_Proj_Remove\"}"))
            .GetProperty("bindingType").GetString()).IsEqualTo("instance");

        // Apply on an empty unused binding: no force needed, read-back confirms.
        var applied = tool.ExecuteForTests(
            Parse("{\"name\":\"SC_Proj_Remove\",\"dryRun\":false}"),
            approved: true, CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();
        await Assert.That(Data(applied).GetProperty("verifiedRemoved").GetBoolean()).IsTrue();
        var gone = List("{\"parameterNameContains\":\"SC_Proj_Remove\"}");
        await Assert.That(Data(gone).GetProperty("total").GetInt32()).IsEqualTo(0);
        // The shared definition element survives a plain unbind.
        await Assert.That(SharedParameterElement.Lookup(Doc, guid)).IsNotNull();

        // deleteDefinition is gated behind force even on a clean binding.
        SeedDefinition("SC_Proj_RemoveDef", SpecTypeId.String.Text);
        var defAdd = Add(
            "{\"name\":\"SC_Proj_RemoveDef\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"binding\":\"type\",\"dryRun\":false}");
        await Assert.That(defAdd.Success).IsTrue();

        var gated = tool.ExecuteForTests(
            Parse("{\"name\":\"SC_Proj_RemoveDef\",\"deleteDefinition\":true,\"dryRun\":false}"),
            approved: true, CancellationToken.None);
        await Assert.That(gated.Success).IsFalse();
        await Assert.That(gated.ErrorCode).IsEqualTo("definition_delete_requires_force");

        var defGuid = Guid.Parse(SingleRow(List("{\"parameterNameContains\":\"SC_Proj_RemoveDef\"}"))
            .GetProperty("guid").GetString()!);
        var forced = tool.ExecuteForTests(
            Parse("{\"name\":\"SC_Proj_RemoveDef\",\"deleteDefinition\":true," +
                  "\"force\":true,\"dryRun\":false}"),
            approved: true, CancellationToken.None);
        await Assert.That(forced.Success).IsTrue();
        await Assert.That(SharedParameterElement.Lookup(Doc, defGuid)).IsNull();
        var defGone = List("{\"parameterNameContains\":\"SC_Proj_RemoveDef\"}");
        await Assert.That(Data(defGone).GetProperty("total").GetInt32()).IsEqualTo(0);

        var missing = tool.ExecuteForTests(
            Parse("{\"name\":\"SC_Proj_NoSuch\",\"dryRun\":true}"),
            approved: true, CancellationToken.None);
        await Assert.That(missing.Success).IsFalse();
        await Assert.That(missing.ErrorCode).IsEqualTo("parameter_not_found");
    }

    [Test]
    public async Task Add_SharedFamilyParameter_ReportsProjectBindingAndDivergence()
    {
        // S.53 D3: a project binding overrides the family's scope and group in
        // the project context; the add result must carry projectBinding and a
        // divergence warning (schedules follow the BINDING's scope).
        SeedDefinition("SC_Proj_BindNote", SpecTypeId.String.Text);
        var bind = Add(
            "{\"name\":\"SC_Proj_BindNote\",\"spec\":\"text\"," +
            "\"categories\":[\"OST_PipeCurves\"],\"binding\":\"instance\",\"dryRun\":false}");
        await Assert.That(bind.Success).IsTrue();
        var guid = SingleRow(List("{\"parameterNameContains\":\"SC_Proj_BindNote\"}"))
            .GetProperty("guid").GetString()!;

        var seed = FamilySeed.Create(Application, Doc, "SC_AgentBindNote", out var reason);
        if (seed is null)
        {
            Skip.Test(reason ?? "Не удалось сидировать семейство");
            return;
        }

        try
        {
            var add = new AddFamilyParameterTool(
                    new StubRevitContext(Doc), _transactions!, _changeSets!)
                .ExecuteForTests(
                    Parse("{\"familyId\":" + seed.FamilyId + ",\"name\":\"SC_Proj_BindNote\"," +
                          "\"storageType\":\"text\",\"isShared\":true,\"guid\":\"" + guid +
                          "\",\"isInstance\":false,\"dryRun\":false}"),
                    approved: true,
                    CancellationToken.None);
            await Assert.That(add.Success).IsTrue();

            var parameter = Data(add).GetProperty("parameter");
            var projectBinding = parameter.GetProperty("projectBinding");
            await Assert.That(projectBinding.GetProperty("scope").GetString())
                .IsEqualTo("instance");
            await Assert.That(projectBinding.TryGetProperty("group", out _)).IsTrue();
            await Assert.That(add.Warnings.Any(warning =>
                warning.Contains("diverge") && warning.Contains("binding"))).IsTrue();
        }
        finally
        {
            seed.Dispose();
        }
    }

    private void SeedDefinition(string name, ForgeTypeId spec)
    {
        var file = Application.OpenSharedParameterFile();
        var group = file!.Groups.get_Item(SeedGroup) ?? file.Groups.Create(SeedGroup);
        group!.Definitions.Create(new ExternalDefinitionCreationOptions(name, spec));
    }

    private ToolResult Add(string json) =>
        new AddProjectSharedParameterTool(
                new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(Parse(json), approved: true, CancellationToken.None);

    private ToolResult List(string json) =>
        new ListProjectParametersTool(new StubRevitContext(Doc))
            .ExecuteForTests(Doc, Parse(json), CancellationToken.None);

    private ToolResult Undo() =>
        new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");

    private static JsonElement SingleRow(ToolResult result)
    {
        var parameters = Data(result).GetProperty("parameters");
        if (parameters.GetArrayLength() != 1)
            throw new InvalidOperationException(
                "Expected exactly one project parameter row, got " + parameters.GetArrayLength());

        return parameters[0];
    }

    private sealed class SharedFileScope : IDisposable
    {
        private readonly Autodesk.Revit.ApplicationServices.Application _application;
        private readonly string? _previous;
        private readonly string? _temporary;

        private SharedFileScope(
            Autodesk.Revit.ApplicationServices.Application application,
            string temporary)
        {
            _application = application;
            _previous = application.SharedParametersFilename;
            _temporary = temporary;
        }

        public static SharedFileScope Create(
            Autodesk.Revit.ApplicationServices.Application application)
        {
            var directory = Path.Combine(Path.GetTempPath(), "SmartConProjectParameterTests");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "shared-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, string.Empty);
            var scope = new SharedFileScope(application, path);
            application.SharedParametersFilename = path;
            return scope;
        }

        public void Dispose()
        {
            try
            {
                _application.SharedParametersFilename = _previous ?? string.Empty;
            }
            catch (Exception)
            {
                // Restoring the shared parameter file is best-effort in the test host.
            }

            try
            {
                if (_temporary is not null && File.Exists(_temporary))
                    File.Delete(_temporary);
            }
            catch (Exception)
            {
                // Temp file cleanup is best-effort.
            }
        }
    }
}
