using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// C5-01: the name collision trap. Creating an own parameter whose name is the
/// localized display name of a built-in parameter ("Код по классификатору" =
/// UNIFORMAT_CODE / ASSEMBLY_CODE) looks green in the read-back while the
/// built-in stays empty. add_family_parameter and add_project_shared_parameter
/// must refuse such names and point at the built-in parameter instead.
/// </summary>
public sealed class AgentParameterNameGuardTests : RevitApiTest
{
    private const string BuiltInLocalizedRu = "Код по классификатору";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private FamilySeed? _seed;
    private SharedFileScope? _sharedFile;

    private Document Doc => _document!;

    private FamilySeed Seed => _seed!;

    [Before(Test)]
    public void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _sharedFile = SharedFileScope.Create(Application);

        // Best effort: the family tests skip when the template family is unavailable.
        _seed = FamilySeed.Create(Application, Doc, "SC_NameGuard_Family", out _);
    }

    [After(Test)]
    public void Close()
    {
        _seed?.Dispose();
        _sharedFile?.Dispose();
        Doc.Close(false);
    }

    /// <summary>
    /// The definition exists in the shared parameter file, so without the guard
    /// the call succeeds and binds the trap parameter (the live case 5 shape).
    /// </summary>
    [Test]
    public async Task AddProjectSharedParameter_BuiltInNameCollision_FailsTyped()
    {
        SeedDefinition(BuiltInLocalizedRu);

        var tool = new AddProjectSharedParameterTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"name\":\"" + BuiltInLocalizedRu + "\",\"spec\":\"text\"," +
                  "\"categories\":[\"OST_Walls\"],\"createDefinition\":false,\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("parameter_name_taken");
        await Assert.That(result.Message!.Contains("built-in", StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Message.Contains(
            AgentParameterResolver.AssemblyCodeBuiltIn.ToString(),
            StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// Control: a normal name (no built-in collision) still binds.
    /// </summary>
    [Test]
    public async Task AddProjectSharedParameter_FreeName_StillBinds()
    {
        SeedDefinition("SC_NameGuard_Free");

        var tool = new AddProjectSharedParameterTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"name\":\"SC_NameGuard_Free\",\"spec\":\"text\"," +
                  "\"categories\":[\"OST_Walls\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
    }

    /// <summary>
    /// The family parameter variant of the trap: the tool must refuse the
    /// localized built-in name instead of creating a shadow parameter.
    /// </summary>
    [Test]
    public async Task AddFamilyParameter_BuiltInNameCollision_FailsTyped()
    {
        if (_seed is null)
            Skip.Test("Не удалось сидировать семейство");

        var tool = new AddFamilyParameterTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"familyId\":" + Seed.FamilyId + ",\"name\":\"" + BuiltInLocalizedRu +
                  "\",\"storageType\":\"text\",\"group\":\"PG_IDENTITY_DATA\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("parameter_name_taken");
        await Assert.That(result.Message!.Contains("built-in", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// Control: a normal family parameter name is still added.
    /// </summary>
    [Test]
    public async Task AddFamilyParameter_FreeName_StillAdded()
    {
        if (_seed is null)
            Skip.Test("Не удалось сидировать семейство");

        var tool = new AddFamilyParameterTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"familyId\":" + Seed.FamilyId +
                  ",\"name\":\"SC_NameGuard_Own\",\"storageType\":\"text\"," +
                  "\"group\":\"PG_IDENTITY_DATA\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
    }

    private void SeedDefinition(string name)
    {
        var file = Application.OpenSharedParameterFile();
        var group = file!.Groups.get_Item("SmartConTests") ?? file.Groups.Create("SmartConTests");
        group!.Definitions.Create(
            new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text));
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

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
                // The temp file is under %TEMP%; leaving it behind is harmless.
            }
        }
    }
}
