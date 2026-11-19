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
/// Write side of the family editing pack (H1d): add/rename parameters, per-type
/// values and formulas, backup, change set and the response envelope.
/// </summary>
public sealed partial class AgentFamilyWriteToolsTests : RevitApiTest
{
    private Document? _document;
    private FamilySeed? _seed;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;

    private Document Doc => _document!;

    private FamilySeed Seed => _seed!;

    [Before(Test)]
    public void SeedModel()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _seed = FamilySeed.Create(Application, Doc, "SC_AgentFamilyWrite", out var reason);
        if (_seed is null)
            Skip.Test(reason ?? "Не удалось сидировать семейство");
    }

    [After(Test)]
    public void Close()
    {
        _seed?.Dispose();
        Doc.Close(false);
    }

    [Test]
    public async Task AddParameter_DryRun_LeavesFamilyUntouched()
    {
        var result = Run(CreateAddTool(), AddJson(dryRun: true));

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.TryGetProperty("changeSetId", out _)).IsFalse();
        await Assert.That(data.TryGetProperty("backupPath", out _)).IsFalse();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);

        var parameter = FindParameterOrNull(await ReadParameters(), "Заказчик");
        await Assert.That(parameter is null).IsTrue();
    }

    [Test]
    public async Task AddParameter_Apply_CreatesParameterAndChangeSet()
    {
        var result = Run(CreateAddTool(), AddJson(dryRun: false));

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        var changeSetId = data.GetProperty("changeSetId").GetString();
        await Assert.That(string.IsNullOrEmpty(changeSetId)).IsFalse();

        var previousFamilyId = data.GetProperty("previousFamilyId").GetInt64();
        var familyId = data.GetProperty("familyId").GetInt64();
        await Assert.That(previousFamilyId).IsGreaterThan(0);
        await Assert.That(familyId).IsGreaterThan(0);
        await Assert.That(data.GetProperty("familyIdChanged").GetBoolean())
            .IsEqualTo(familyId != previousFamilyId);
        await Assert.That(string.IsNullOrWhiteSpace(
                data.GetProperty("familyUniqueId").GetString()))
            .IsFalse();
        var reloaded = Doc.GetElement(
            SmartCon.Core.Compatibility.ElementIdCompat.Create(familyId)) as Family;
        await Assert.That(reloaded).IsNotNull();
        await Assert.That(reloaded!.Name).IsEqualTo(Seed.FamilyName);

        var backupPath = data.GetProperty("backupPath").GetString();
        await Assert.That(backupPath).IsNotNull();
        await Assert.That(File.Exists(backupPath!)).IsTrue();

        var changeSet = _changeSets!.Last;
        await Assert.That(changeSet).IsNotNull();
        await Assert.That(changeSet!.Header.Operation).IsEqualTo("add_family_parameter");
        await Assert.That(changeSet.Items.Count).IsEqualTo(1);
        await Assert.That(changeSet.Items[0].ParameterName).IsEqualTo("family");
        using var plan = JsonDocument.Parse(changeSet.Items[0].OldValue!);
        await Assert.That(plan.RootElement.GetProperty("backupPath").GetString())
            .IsEqualTo(backupPath);
        await Assert.That(
            plan.RootElement.GetProperty("ops")[0].GetProperty("kind").GetString())
            .IsEqualTo("add");

        var parameter = FindParameterOrNull(await ReadParameters(), "Заказчик");
        await Assert.That(parameter is not null).IsTrue();
        await Assert.That(parameter!.Value.GetProperty("scope").GetString()).IsEqualTo("type");
        await Assert.That(parameter.Value.GetProperty("isShared").GetBoolean()).IsFalse();

        var sections = data.GetProperty("sections");
        var hasReadBack = false;
        foreach (var section in sections.EnumerateArray())
        {
            if (section.GetProperty("section").GetString() == "readBack")
                hasReadBack = true;
        }

        await Assert.That(hasReadBack).IsTrue();
        await Assert.That(
            data.GetProperty("readBack").GetProperty("mismatched").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task AddParameter_ByFamilyUniqueId_ResolvesFamily()
    {
        var family = new FilteredElementCollector(Doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(item => string.Equals(
                item.Name, Seed.FamilyName, StringComparison.OrdinalIgnoreCase));

        var result = Run(
            CreateAddTool(),
            "{\"familyUniqueId\":\"" + family.UniqueId + "\"," +
            "\"name\":\"ЗаказчикUnique\",\"storageType\":\"text\",\"dryRun\":true}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Data(result).GetProperty("familyUniqueId").GetString())
            .IsEqualTo(family.UniqueId);
    }

    [Test]
    public async Task AddParameter_DuctSizeStorageType_CreatesParameter()
    {
        var result = Run(
            CreateAddTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\"," +
            "\"name\":\"DuctSizeAdded\",\"storageType\":\"ductsize\",\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var parameter = FindParameterOrNull(await ReadParameters(), "DuctSizeAdded");
        await Assert.That(parameter is not null).IsTrue();
        await Assert.That(parameter!.Value.GetProperty("spec").GetString())
            .IsEqualTo("ductSize");
    }

    [Test]
    public async Task AddParameter_Duplicate_ReturnsParameterExists()
    {
        Run(CreateAddTool(), AddJson(dryRun: false));

        var result = Run(CreateAddTool(), AddJson(dryRun: true));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("parameter_exists");
    }

    [Test]
    public async Task AddParameter_Shared_CreatesGuidAndRejectsRename()
    {
        using var sharedScope = CreateSharedFileScope();
        var guid = Guid.NewGuid();

        var result = Run(
            CreateAddTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"name\":\"ОбщийКод\"," +
            "\"storageType\":\"text\",\"group\":\"PG_IDENTITY_DATA\",\"isShared\":true," +
            "\"guid\":\"" + guid.ToString("D") + "\",\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(
            data.GetProperty("parameter").GetProperty("guid").GetString())
            .IsEqualTo(guid.ToString("D"));

        var parameter = FindParameterOrNull(await ReadParameters(), "ОбщийКод");
        await Assert.That(parameter).IsNotNull();
        await Assert.That(parameter!.Value.GetProperty("isShared").GetBoolean()).IsTrue();
        await Assert.That(parameter.Value.GetProperty("guid").GetString())
            .IsEqualTo(guid.ToString("D"));

        var rename = Run(
            CreateRenameTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"ОбщийКод\"," +
            "\"newName\":\"ОбщийКод2\",\"dryRun\":false}");

        await Assert.That(rename.Success).IsFalse();
        await Assert.That(rename.ErrorCode).IsEqualTo("shared_parameter_not_renameable");
    }

    [Test]
    public async Task AddParameter_SharedWithoutSharedFile_ReturnsSharedParameterFileMissing()
    {
        using var sharedScope = SharedFileScope.PointToMissingFile(Application);

        var result = Run(
            CreateAddTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"name\":\"ОбщийКод\"," +
            "\"storageType\":\"text\",\"isShared\":true,\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("shared_parameter_file_missing");
    }

    [Test]
    public async Task RenameParameter_DryRun_ReportsDiffWithoutChangingFamily()
    {
        var result = Run(
            CreateRenameTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"Марка\"," +
            "\"newName\":\"МаркаИзделия\",\"dryRun\":true}");

        await Assert.That(result.Success).IsTrue();
        var changed = Data(result).GetProperty("changed");
        await Assert.That(changed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(changed[0].GetProperty("oldValue").GetString()).IsEqualTo("Марка");
        await Assert.That(changed[0].GetProperty("newValue").GetString())
            .IsEqualTo("МаркаИзделия");

        await Assert.That(FindParameterOrNull(await ReadParameters(), "Марка")).IsNotNull();
        await Assert.That(FindParameterOrNull(await ReadParameters(), "МаркаИзделия")).IsNull();
    }

    [Test]
    public async Task RenameParameter_Apply_RenamesAndReadsBack()
    {
        var result = Run(
            CreateRenameTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"Марка\"," +
            "\"newName\":\"МаркаИзделия\",\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("readBack").GetProperty("mismatched").GetInt32())
            .IsEqualTo(0);
        await Assert.That(data.TryGetProperty("changeSetId", out _)).IsTrue();

        var parameters = await ReadParameters();
        await Assert.That(FindParameterOrNull(parameters, "МаркаИзделия")).IsNotNull();
        await Assert.That(FindParameterOrNull(parameters, "Марка")).IsNull();
    }

    [Test]
    public async Task RenameParameter_DuplicateName_ReturnsDuplicateName()
    {
        var result = Run(
            CreateRenameTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"Марка\"," +
            "\"newName\":\"Ширина\",\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("duplicate_name");
    }

    [Test]
    public async Task RenameParameter_UnknownParameter_ReturnsParameterNotFound()
    {
        var result = Run(
            CreateRenameTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"НетТакого\"," +
            "\"newName\":\"Новое\",\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("parameter_not_found");
    }

    [Test]
    public async Task SetValues_MissingUnit_ReturnsUnitRequired()
    {
        var result = Run(
            CreateSetValuesTool(),
            Items("\"parameter\":\"Ширина\",\"typeName\":\"Тип1\",\"value\":\"1500\"", false));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unit_required");
        await Assert.That(Data(result).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("unit_required");
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);

        var width = Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
        await Assert.That(System.Math.Abs(width!.AsDouble() - FamilySeed.Type1WidthMm / 304.8) < 1e-9).IsTrue();
    }

    [Test]
    public async Task SetValues_ApplyPerType_ReadsBackProjectValues()
    {
        var items =
            "\"items\":[" +
            "{\"parameter\":\"Ширина\",\"typeName\":\"Тип1\",\"value\":\"1100\",\"unit\":\"mm\"}," +
            "{\"parameter\":\"Высота\",\"typeName\":\"Тип2\",\"value\":\"1900\",\"unit\":\"mm\"}" +
            "],\"dryRun\":false";

        var result = Run(CreateSetValuesTool(), "{\"familyId\":" + Seed.FamilyId + "," + items + "}");

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(2);
        await Assert.That(data.GetProperty("readBack").GetProperty("verified").GetInt32())
            .IsEqualTo(2);
        await Assert.That(data.GetProperty("readBack").GetProperty("mismatched").GetInt32())
            .IsEqualTo(0);
        await Assert.That(_changeSets!.Last!.Header.Operation)
            .IsEqualTo("set_family_parameter_values");

        var width = Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
        var height = Seed.GetProjectParameter(FamilySeed.Type2, FamilySeed.HeightParam);
        await Assert.That(System.Math.Abs(width!.AsDouble() - 1100.0 / 304.8) < 1e-9).IsTrue();
        await Assert.That(System.Math.Abs(height!.AsDouble() - 1900.0 / 304.8) < 1e-9).IsTrue();
    }

    [Test]
    public async Task SetValues_DuctSizeAndPipeSize_AcceptMillimetres()
    {
        var items =
            "\"items\":[" +
            "{\"parameter\":\"" + FamilySeed.DuctSizeParam +
            "\",\"typeName\":\"Тип1\",\"value\":\"450\",\"unit\":\"mm\"}," +
            "{\"parameter\":\"" + FamilySeed.PipeSizeParam +
            "\",\"typeName\":\"Тип1\",\"value\":\"50\",\"unit\":\"mm\"}" +
            "],\"dryRun\":false";

        var result = Run(
            CreateSetValuesTool(),
            "{\"familyId\":" + Seed.FamilyId + "," + items + "}");

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(2);
        await Assert.That(data.GetProperty("readBack").GetProperty("verified").GetInt32())
            .IsEqualTo(2);

        var duct = Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.DuctSizeParam);
        var pipe = Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.PipeSizeParam);
        await Assert.That(System.Math.Abs(duct!.AsDouble() - 450.0 / 304.8) < 1e-9).IsTrue();
        await Assert.That(System.Math.Abs(pipe!.AsDouble() - 50.0 / 304.8) < 1e-9).IsTrue();
    }

    [Test]
    public async Task SetValues_FormulaParameter_ReturnsFormulaNotAllowed()
    {
        var result = Run(
            CreateSetValuesTool(),
            Items(
                "\"parameter\":\"Площадь\",\"typeName\":\"Тип1\",\"value\":\"10\",\"unit\":\"m2\"",
                false));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(Data(result).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("formula_not_allowed");
    }

    [Test]
    public async Task SetValues_ScopeMismatch_ReturnsScopeMismatch()
    {
        var result = Run(
            CreateSetValuesTool(),
            Items(
                "\"parameter\":\"Примечание_экз\",\"scope\":\"type\",\"typeName\":\"Тип1\"," +
                "\"value\":\"текст\"",
                false));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(Data(result).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("scope_mismatch");
    }

    [Test]
    public async Task SetValues_UnknownType_ReturnsTypeNotFound()
    {
        var result = Run(
            CreateSetValuesTool(),
            Items(
                "\"parameter\":\"Ширина\",\"typeName\":\"НетТакого\",\"value\":\"1000\"," +
                "\"unit\":\"mm\"",
                false));

        await Assert.That(result.Success).IsFalse();
        await Assert.That(Data(result).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("type_not_found");
    }

    [Test]
    public async Task SetValues_SameValue_IsReportedAsSkipped()
    {
        var result = Run(
            CreateSetValuesTool(),
            Items(
                "\"parameter\":\"Ширина\",\"typeName\":\"Тип1\",\"value\":\"1000\",\"unit\":\"mm\"",
                false));

        await Assert.That(result.Success).IsTrue();
        var data = Data(result);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(0);
        await Assert.That(data.GetProperty("skipped").GetArrayLength()).IsEqualTo(1);
        await Assert.That(result.Message!).Contains("No changes");
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetFormula_DryRun_ShowsBeforeAfterPerType()
    {
        var result = Run(
            CreateFormulaTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"Площадь\"," +
            "\"formula\":\"Ширина * Высота * 2\",\"dryRun\":true}");

        await Assert.That(result.Success).IsTrue();
        var changed = Data(result).GetProperty("changed");
        await Assert.That(changed.GetArrayLength()).IsGreaterThanOrEqualTo(3);

        var area = FindParameterOrNull(await ReadParameters(), "Площадь");
        await Assert.That(area!.Value.GetProperty("formula").GetString())
            .IsEqualTo("Ширина * Высота");
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetFormula_ApplyAndClear_RoundTrips()
    {
        var apply = Run(
            CreateFormulaTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"Площадь\"," +
            "\"formula\":\"Ширина * Высота * 2\",\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        await Assert.That(Data(apply).GetProperty("readBack")
            .GetProperty("mismatched").GetInt32()).IsEqualTo(0);
        await Assert.That(Data(apply).TryGetProperty("changeSetId", out _)).IsTrue();

        var area = Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.AreaParam);
        var expected = (1000.0 / 304.8) * (2000.0 / 304.8) * 2.0;
        await Assert.That(System.Math.Abs(area!.AsDouble() - expected) < 1e-6).IsTrue();

        var clear = Run(
            CreateFormulaTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"Площадь\"," +
            "\"formula\":\"\",\"dryRun\":false}");

        await Assert.That(clear.Success).IsTrue();
        var cleared = FindParameterOrNull(await ReadParameters(), "Площадь");
        await Assert.That(cleared).IsNotNull();
        await Assert.That(
            cleared!.Value.TryGetProperty("formula", out var formulaElement) &&
            formulaElement.ValueKind != System.Text.Json.JsonValueKind.Null).IsFalse();
        await Assert.That(cleared.Value.GetProperty("isDeterminedByFormula").GetBoolean())
            .IsFalse();
    }

    [Test]
    public async Task SetFormula_MissingLookupTable_ReturnsLookupTableMissing()
    {
        var result = Run(
            CreateFormulaTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"Размер\"," +
            "\"formula\":\"size_lookup(\\\"NoSuchTable\\\", \\\"A\\\", 0 mm, Ширина)\"," +
            "\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("lookup_table_missing");
    }

    [Test]
    public async Task SetFormula_InvalidSyntax_ReturnsFormulaError()
    {
        var result = Run(
            CreateFormulaTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"Размер\"," +
            "\"formula\":\"Ширина * * 2\",\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(
            result.ErrorCode == "invalid_formula" ||
            result.ErrorCode == "formula_not_allowed").IsTrue();

        var parameter = FindParameterOrNull(await ReadParameters(), "Размер");
        await Assert.That(parameter).IsNotNull();
        await Assert.That(
            parameter!.Value.TryGetProperty("formula", out var currentFormula)).IsFalse();
    }

    [Test]
    public async Task AddParameter_HugeName_IsRejectedGracefully()
    {
        var hugeName = new string('X', 5000);
        var result = Run(
            CreateAddTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"name\":\"" + hugeName + "\"," +
            "\"storageType\":\"text\",\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsNotNull();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetFormula_NonAssignableParameter_ReturnsFormulaNotAllowed()
    {
        var parameters = await ReadParameters();
        string? readOnlyName = null;
        foreach (var parameter in parameters.GetProperty("parameters").EnumerateArray())
        {
            if (!parameter.GetProperty("canSetFormula").GetBoolean()
                && !parameter.GetProperty("isDeterminedByFormula").GetBoolean())
            {
                readOnlyName = parameter.GetProperty("name").GetString();
                break;
            }
        }

        if (readOnlyName is null)
            Skip.Test("В семействе нет параметра без права на формулу");

        var result = Run(
            CreateFormulaTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"parameter\":\"" + readOnlyName +
            "\",\"formula\":\"1\",\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("formula_not_allowed");
    }

    [Test]
    public async Task AddParameter_WithoutFamilyReference_ReturnsInvalidArguments()
    {
        var result = Run(
            CreateAddTool(),
            "{\"name\":\"Заказчик\",\"storageType\":\"text\",\"dryRun\":true}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
    }

    [Test]
    public async Task FamilyTools_CloseFamilyDocumentOnSuccessAndFailure()
    {
        var before = Application.Documents.Size;

        var failure = Run(
            CreateSetValuesTool(),
            Items("\"parameter\":\"НетТакого\",\"value\":\"1\"", true));

        var success = Run(CreateAddTool(), AddJson(dryRun: true));

        await Assert.That(failure.Success).IsFalse();
        await Assert.That(success.Success).IsTrue();
        await Assert.That(Application.Documents.Size).IsEqualTo(before);
    }

    [Test]
    public async Task AddParameter_DryRunShared_DoesNotCreateDefinition()
    {
        using var scope = CreateSharedFileScope();
        var path = scope.FilePath!;
        var before = new FileInfo(path).Length;

        var result = Run(
            CreateAddTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"name\":\"ОбщийКод\"," +
            "\"storageType\":\"text\",\"isShared\":true,\"dryRun\":true}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(
            Data(result).GetProperty("parameter")
                .GetProperty("wouldCreateDefinition").GetBoolean()).IsTrue();
        await Assert.That(new FileInfo(path).Length).IsEqualTo(before);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    private AddFamilyParameterTool CreateAddTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private RenameFamilyParameterTool CreateRenameTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private SetFamilyParameterValuesTool CreateSetValuesTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private SetFamilyFormulaTool CreateFormulaTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private string AddJson(bool dryRun) =>
        "{\"familyId\":" + Seed.FamilyId + ",\"name\":\"Заказчик\"," +
        "\"storageType\":\"text\",\"group\":\"PG_IDENTITY_DATA\",\"dryRun\":" +
        (dryRun ? "true" : "false") + "}";

    private string Items(string item, bool dryRun) =>
        "{\"familyId\":" + Seed.FamilyId + ",\"items\":[{" + item + "}],\"dryRun\":" +
        (dryRun ? "true" : "false") + "}";

    private static ToolResult Run(IAgentTool tool, string json) =>
        ((AgentWriteToolBase)tool).ExecuteForTests(
            Parse(json),
            approved: true,
            CancellationToken.None);

    private async Task<JsonElement> ReadParameters()
    {
        var tool = new GetFamilyParametersTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"familyId\":" + Seed.FamilyId + ",\"includeValues\":false}"),
            CancellationToken.None);
        await Assert.That(result.Success).IsTrue();
        return Data(result);
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");

    private static JsonElement? FindParameterOrNull(JsonElement data, string name)
    {
        foreach (var parameter in data.GetProperty("parameters").EnumerateArray())
        {
            if (string.Equals(parameter.GetProperty("name").GetString(), name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return parameter;
            }
        }

        return null;
    }
}