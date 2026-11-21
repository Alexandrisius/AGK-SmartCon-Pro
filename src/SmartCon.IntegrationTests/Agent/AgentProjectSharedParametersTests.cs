using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
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
