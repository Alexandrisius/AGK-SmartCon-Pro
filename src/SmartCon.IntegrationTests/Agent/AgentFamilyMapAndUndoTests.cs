using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
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
/// map_family_parameters and undo_last for family changes (backup restore).
/// </summary>
public sealed class AgentFamilyMapAndUndoTests : RevitApiTest
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
        _seed = FamilySeed.Create(Application, Doc, "SC_AgentFamilyMap", out var reason);
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
    public async Task MapValues_CopiesPerTypeAndIsIdempotent()
    {
        await AddParameter("Ширина_копия", "length");

        var first = Run(
            CreateMapTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"mappings\":[{\"source\":{\"name\":\"Ширина\"}," +
            "\"target\":{\"name\":\"Ширина_копия\"}}],\"dryRun\":false}");

        await Assert.That(first.Success).IsTrue();
        var data = Data(first);
        await Assert.That(data.GetProperty("changed").GetArrayLength())
            .IsGreaterThanOrEqualTo(2);
        await Assert.That(data.GetProperty("readBack").GetProperty("mismatched").GetInt32())
            .IsEqualTo(0);
        await Assert.That(data.TryGetProperty("changeSetId", out _)).IsTrue();

        var source = Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
        var copy = Seed.GetProjectParameter(FamilySeed.Type1, "Ширина_копия");
        await Assert.That(System.Math.Abs(copy!.AsDouble() - source!.AsDouble()) < 1e-9).IsTrue();

        var changeSetsBefore = _changeSets!.All.Count;
        var second = Run(
            CreateMapTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"mappings\":[{\"source\":{\"name\":\"Ширина\"}," +
            "\"target\":{\"name\":\"Ширина_копия\"}}],\"dryRun\":false}");

        await Assert.That(second.Success).IsTrue();
        await Assert.That(Data(second).GetProperty("changed").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Data(second).GetProperty("skipped").GetArrayLength()).IsEqualTo(2);
        await Assert.That(_changeSets.All.Count).IsEqualTo(changeSetsBefore);
    }

    [Test]
    public async Task MapValues_ScopeMismatch_ReturnsScopeMismatch()
    {
        var result = Run(
            CreateMapTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"mappings\":[{\"source\":{\"name\":\"Ширина\"}," +
            "\"target\":{\"name\":\"Примечание_экз\"}}],\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(Data(result).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("scope_mismatch");
    }

    [Test]
    public async Task MapValues_SpecMismatch_ReturnsSpecMismatch()
    {
        var result = Run(
            CreateMapTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"mappings\":[{\"source\":{\"name\":\"Ширина\"}," +
            "\"target\":{\"name\":\"Марка\"}}],\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(Data(result).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("spec_mismatch");
    }

    [Test]
    public async Task MapValues_UnknownTarget_ReturnsParameterNotFound()
    {
        var result = Run(
            CreateMapTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"mappings\":[{\"source\":{\"name\":\"Ширина\"}," +
            "\"target\":{\"name\":\"НетТакого\"}}],\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(Data(result).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("parameter_not_found");
    }

    [Test]
    public async Task MapFormula_CopiesFormulaAndValues()
    {
        await AddParameter("Площадь_копия", "area");

        var result = Run(
            CreateMapTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"mappings\":[{\"source\":{\"name\":\"Площадь\"}," +
            "\"target\":{\"name\":\"Площадь_копия\"},\"mode\":\"formula\"}],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Data(result).GetProperty("readBack").GetProperty("mismatched")
            .GetInt32()).IsEqualTo(0);

        var source = Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.AreaParam);
        var copy = Seed.GetProjectParameter(FamilySeed.Type1, "Площадь_копия");
        await Assert.That(System.Math.Abs(copy!.AsDouble() - source!.AsDouble()) < 1e-6).IsTrue();

        var parameters = await ReadParameters();
        var target = FindParameter(parameters, "Площадь_копия");
        await Assert.That(target.GetProperty("formula").GetString()).IsEqualTo("Ширина * Высота");
    }

    [Test]
    public async Task MapFormula_DifferentSpec_ReturnsSpecMismatch()
    {
        var result = Run(
            CreateMapTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"mappings\":[{\"source\":{\"name\":\"Площадь\"}," +
            "\"target\":{\"name\":\"Марка\"},\"mode\":\"formula\"}],\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(Data(result).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("spec_mismatch");
    }

    [Test]
    public async Task UndoLast_RestoresFamilyFromBackup()
    {
        var add = Run(CreateAddTool(), AddJson());
        await Assert.That(add.Success).IsTrue();
        var addChangeSet = _changeSets!.Last!;
        await Assert.That(addChangeSet.Header.AppliedAt.HasValue).IsTrue();
        await Assert.That(FindParameterOrNull(await ReadParameters(), "Заказчик")).IsNotNull();

        var undo = Run(CreateUndoTool(), "{\"dryRun\":false}");

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(Data(undo).GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(FindParameterOrNull(await ReadParameters(), "Заказчик")).IsNull();

        var rolledBack = _changeSets.All.Single(
            set => set.Header.Id == addChangeSet.Header.Id);
        await Assert.That(rolledBack.Header.AppliedAt.HasValue).IsFalse();
    }

    [Test]
    public async Task UndoLast_DryRun_DoesNotRestore()
    {
        var add = Run(CreateAddTool(), AddJson());
        await Assert.That(add.Success).IsTrue();

        var undo = Run(CreateUndoTool(), "{\"dryRun\":true}");

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(Data(undo).GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(FindParameterOrNull(await ReadParameters(), "Заказчик")).IsNotNull();
    }

    [Test]
    public async Task UndoLast_MissingBackup_ReturnsBackupUnavailable()
    {
        var missing = Path.Combine(Path.GetTempPath(), "SmartConFamilyTests", "missing-backup.rfa");
        if (File.Exists(missing))
            File.Delete(missing);

        var header = new ChangeSetHeader
        {
            Operation = "add_family_parameter",
            DocumentId = SmartCon.Revit.Agent.AgentDocumentIdentity.GetDocumentId(Doc)
        };
        var changeSetId = await _changeSets!.BeginAsync(header, CancellationToken.None);
        await _changeSets.AddItemsAsync(
            changeSetId,
            new[]
            {
                new ChangeItem
                {
                    ChangeSetId = changeSetId,
                    ParameterName = "family",
                    OldValue = missing,
                    NewValue = Seed.FamilyName,
                    Status = ChangeStatus.Applied
                }
            },
            CancellationToken.None);
        await _changeSets.MarkAppliedAsync(
            changeSetId,
            DateTime.UtcNow,
            CancellationToken.None);

        var undo = Run(CreateUndoTool(), "{\"dryRun\":false}");

        await Assert.That(undo.Success).IsFalse();
        await Assert.That(undo.ErrorCode).IsEqualTo("backup_unavailable");
    }

    [Test]
    public async Task UndoLast_AfterRename_RestoresOldName()
    {
        var rename = Run(
            CreateRenameTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"parameter\":\"Марка\"," +
            "\"newName\":\"МаркаИзделия\",\"dryRun\":false}");
        await Assert.That(rename.Success).IsTrue();

        var undo = Run(CreateUndoTool(), "{\"dryRun\":false}");

        await Assert.That(undo.Success).IsTrue();
        var parameters = await ReadParameters();
        await Assert.That(FindParameterOrNull(parameters, "Марка")).IsNotNull();
        await Assert.That(FindParameterOrNull(parameters, "МаркаИзделия")).IsNull();
    }

    [Test]
    public async Task UndoLast_AfterValueSet_RestoresOldValue()
    {
        var set = Run(
            CreateSetValuesTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"items\":[" +
            "{\"parameter\":\"Ширина\",\"typeName\":\"Тип1\",\"value\":\"1100\"," +
            "\"unit\":\"mm\"}],\"dryRun\":false}");
        await Assert.That(set.Success).IsTrue();

        var undo = Run(CreateUndoTool(), "{\"dryRun\":false}");

        await Assert.That(undo.Success).IsTrue();
        var width = Seed.GetProjectParameter(FamilySeed.Type1, FamilySeed.WidthParam);
        await Assert.That(System.Math.Abs(width!.AsDouble() - 1000.0 / 304.8) < 1e-9).IsTrue();
    }

    [Test]
    public async Task UndoLast_AfterFormulaChange_RestoresFormula()
    {
        var formula = Run(
            CreateFormulaTool(),
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"parameter\":\"Площадь\"," +
            "\"formula\":\"Ширина * Высота * 2\",\"dryRun\":false}");
        await Assert.That(formula.Success).IsTrue();

        var undo = Run(CreateUndoTool(), "{\"dryRun\":false}");

        await Assert.That(undo.Success).IsTrue();
        var parameter = FindParameter(await ReadParameters(), FamilySeed.AreaParam);
        await Assert.That(parameter.GetProperty("formula").GetString())
            .IsEqualTo("Ширина * Высота");
    }

    [Test]
    public async Task UndoLast_LegacyBackup_RestoresFamily()
    {
        var header = new ChangeSetHeader
        {
            Operation = "add_family_parameter",
            DocumentId = SmartCon.Revit.Agent.AgentDocumentIdentity.GetDocumentId(Doc)
        };
        var changeSetId = await _changeSets!.BeginAsync(header, CancellationToken.None);
        await _changeSets.AddItemsAsync(
            changeSetId,
            new[]
            {
                new ChangeItem
                {
                    ChangeSetId = changeSetId,
                    ParameterName = "family",
                    OldValue = Seed.RfaPath,
                    NewValue = Seed.FamilyName,
                    Status = ChangeStatus.Applied
                }
            },
            CancellationToken.None);
        await _changeSets.MarkAppliedAsync(changeSetId, DateTime.UtcNow, CancellationToken.None);

        var undo = Run(CreateUndoTool(), "{\"dryRun\":false}");

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(Data(undo).GetProperty("changed").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task UndoLast_PartialFamilyFailure_KeepsChangeSetApplied()
    {
        var header = new ChangeSetHeader
        {
            Operation = "set_family_parameter_values",
            DocumentId = SmartCon.Revit.Agent.AgentDocumentIdentity.GetDocumentId(Doc)
        };
        var changeSetId = await _changeSets!.BeginAsync(header, CancellationToken.None);
        var plan =
            "{\"familyName\":\"" + Seed.FamilyName + "\",\"ops\":[" +
            "{\"kind\":\"value\",\"parameter\":\"Ширина\",\"scope\":\"type\"," +
            "\"typeName\":\"Тип1\",\"storage\":\"double\",\"value\":\"1\"}," +
            "{\"kind\":\"bogus\",\"parameter\":\"X\"}]}";
        await _changeSets.AddItemsAsync(
            changeSetId,
            new[]
            {
                new ChangeItem
                {
                    ChangeSetId = changeSetId,
                    ParameterName = "family",
                    OldValue = plan,
                    NewValue = Seed.FamilyName,
                    Status = ChangeStatus.Applied
                }
            },
            CancellationToken.None);
        await _changeSets.MarkAppliedAsync(changeSetId, DateTime.UtcNow, CancellationToken.None);

        var undo = Run(CreateUndoTool(), "{\"dryRun\":false}");

        await Assert.That(undo.Success).IsFalse();
        await Assert.That(undo.ErrorCode).IsEqualTo("family_undo_partial");
        var data = Data(undo);
        await Assert.That(data.GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("failed").GetArrayLength()).IsEqualTo(1);

        var stored = _changeSets.All.Single(set => set.Header.Id == changeSetId);
        await Assert.That(stored.Header.AppliedAt.HasValue).IsTrue();
    }

    private async Task AddParameter(string name, string storageType)
    {
        var result = Run(
            CreateAddTool(),
            "{\"familyId\":" + Seed.FamilyId + ",\"name\":\"" + name + "\"," +
            "\"storageType\":\"" + storageType + "\",\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
    }

    private AddFamilyParameterTool CreateAddTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private MapFamilyParametersTool CreateMapTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private RenameFamilyParameterTool CreateRenameTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private SetFamilyParameterValuesTool CreateSetValuesTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private SetFamilyFormulaTool CreateFormulaTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private UndoLastTool CreateUndoTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private string AddJson() =>
        "{\"familyId\":" + Seed.FamilyId + ",\"name\":\"Заказчик\"," +
        "\"storageType\":\"text\",\"group\":\"PG_IDENTITY_DATA\",\"dryRun\":false}";

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

    private static ToolResult Run(IAgentTool tool, string json) =>
        tool is AgentWriteToolBase writeTool
            ? writeTool.ExecuteForTests(Parse(json), approved: true, CancellationToken.None)
            : throw new InvalidOperationException("Expected a write tool.");

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

    private static JsonElement FindParameter(JsonElement data, string name) =>
        FindParameterOrNull(data, name)
        ?? throw new InvalidOperationException("Parameter '" + name + "' is missing.");
}