using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

public sealed class AgentMepTests : RevitApiTest
{
    private const string SeededSystemName = "SC_Agent_Mep_System";
    private const string SeededAbbreviation = "SCA";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _systemId;
    private long _systemTypeId;
    private bool _systemCreated;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        try
        {
            _transactions.RunInTransaction(Doc, "Seed agent mep model", doc =>
            {
                var systemTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipingSystemType))
                    .WhereElementIsElementType()
                    .FirstElementId();
                if (systemTypeId.Equals(ElementId.InvalidElementId))
                    return;

                var system = PipingSystem.Create(doc, systemTypeId, SeededSystemName);
                _systemId = system.Id.GetValue();
                _systemTypeId = systemTypeId.GetValue();
                _systemCreated = true;
            });
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // The default template may have the MEP disciplines disabled — the
            // system cases skip honestly instead of failing the session.
            _systemCreated = false;
        }
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task ListMepSystems_ReturnsSeededPipingSystem()
    {
        if (!_systemCreated)
            Skip.Test("В шаблоне проекта нет PipingSystemType или MEP-дисциплина отключена");

        var tool = new ListMepSystemsTool(new StubRevitContext(Doc));
        var args = Parse("{\"domain\":\"piping\",\"nameContains\":\"" + SeededSystemName + "\"}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var systems = result.Data!.Value.GetProperty("systems");
        await Assert.That(systems.GetArrayLength()).IsEqualTo(1);

        var card = systems[0];
        await Assert.That(card.GetProperty("systemId").GetInt64()).IsEqualTo(_systemId);
        await Assert.That(card.GetProperty("name").GetString()).IsEqualTo(SeededSystemName);
        await Assert.That(card.GetProperty("domain").GetString()).IsEqualTo("piping");
        await Assert.That(card.GetProperty("classification").GetString()).IsNotNull();
        await Assert.That(card.GetProperty("typeId").GetInt64()).IsEqualTo(_systemTypeId);
        await Assert.That(card.GetProperty("elementCount").GetInt32()).IsEqualTo(0);
        await Assert.That(card.GetProperty("equipmentCount").GetInt32()).IsEqualTo(0);
        await Assert.That(card.GetProperty("isEmpty").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task ListMepSystems_ElectricalFilter_EmptyListIsOk()
    {
        var tool = new ListMepSystemsTool(new StubRevitContext(Doc));
        var args = Parse("{\"domain\":\"electrical\"}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("total").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("systems").GetArrayLength()).IsEqualTo(0);
        await Assert.That(data.GetProperty("truncated").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task ListMepSystems_FieldsProjectCard()
    {
        if (!_systemCreated)
            Skip.Test("В шаблоне проекта нет PipingSystemType или MEP-дисциплина отключена");

        var tool = new ListMepSystemsTool(new StubRevitContext(Doc));
        var args = Parse(
            "{\"nameContains\":\"" + SeededSystemName + "\",\"fields\":[\"systemId\",\"abbreviation\"]}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var card = result.Data!.Value.GetProperty("systems")[0];
        await Assert.That(card.EnumerateObject().Count()).IsEqualTo(2);
        await Assert.That(card.GetProperty("systemId").GetInt64()).IsEqualTo(_systemId);
        await Assert.That(card.TryGetProperty("name", out _)).IsFalse();
    }

    [Test]
    public async Task ListMepSystems_UnknownDomain_Fails()
    {
        var tool = new ListMepSystemsTool(new StubRevitContext(Doc));
        var args = Parse("{\"domain\":\"hydronic\"}");

        var result = tool.ExecuteForTests(Doc, args, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unknown_domain");
    }

    [Test]
    public async Task ListMaterials_ReturnsTemplateMaterialsWithShape()
    {
        var tool = new ListMaterialsTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var data = result.Data!.Value;
        await Assert.That(data.GetProperty("total").GetInt32()).IsGreaterThanOrEqualTo(1);

        var card = data.GetProperty("materials")[0];
        await Assert.That(card.GetProperty("materialId").GetInt64()).IsGreaterThan(0);
        await Assert.That(card.GetProperty("name").GetString()).IsNotNull();
        await Assert.That(card.EnumerateObject().Select(p => p.Name))
            .Contains("materialClass");
    }

    [Test]
    public async Task ListMaterials_NameContains_FiltersList()
    {
        var tool = new ListMaterialsTool(new StubRevitContext(Doc));

        var all = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);
        await Assert.That(all.Success).IsTrue();
        var first = all.Data!.Value.GetProperty("materials")[0];
        var firstName = first.GetProperty("name").GetString()!;

        var filtered = tool.ExecuteForTests(
            Doc, Parse("{\"nameContains\":\"" + firstName + "\"}"), CancellationToken.None);

        await Assert.That(filtered.Success).IsTrue();
        await Assert.That(filtered.Data!.Value.GetProperty("total").GetInt32())
            .IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task ListMaterials_Paging_LimitsAndReportsNextOffset()
    {
        var tool = new ListMaterialsTool(new StubRevitContext(Doc));

        var all = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);
        await Assert.That(all.Success).IsTrue();
        if (all.Data!.Value.GetProperty("total").GetInt32() < 2)
            Skip.Test("В шаблоне проекта меньше двух материалов — пагинация не проверяема");

        var page = tool.ExecuteForTests(
            Doc, Parse("{\"limit\":1}"), CancellationToken.None);

        await Assert.That(page.Success).IsTrue();
        var data = page.Data!.Value;
        await Assert.That(data.GetProperty("returned").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("truncated").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("nextOffset").GetInt32()).IsEqualTo(1);

        var second = tool.ExecuteForTests(
            Doc, Parse("{\"limit\":1,\"offset\":1}"), CancellationToken.None);
        await Assert.That(second.Success).IsTrue();
        await Assert.That(second.Data!.Value.GetProperty("returned").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task SetParameters_SystemAbbreviation_IsReadOnlyMirrorAndRefused()
    {
        if (!_systemCreated)
            Skip.Test("В шаблоне проекта нет PipingSystemType или MEP-дисциплина отключена");

        var before = ReadAbbreviation();

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _systemId +
            ",\"parameter\":\"RBS_DUCT_PIPE_SYSTEM_ABBREVIATION_PARAM\",\"value\":\"" +
            SeededAbbreviation + "\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        // The abbreviation parameter on the MEPSystem instance is a read-only mirror
        // of the system type (which stores it as a property, not a parameter):
        // set_parameters must refuse it instead of silently changing nothing.
        await Assert.That(result.Success).IsFalse();
        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("readonly_parameter");
        await Assert.That(ReadAbbreviation()).IsEqualTo(before);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetParameters_MaterialTransparency_AppliesReadsBackAndUndoes()
    {
        var materialId = FirstMaterialId();
        if (materialId == 0)
            Skip.Test("В шаблоне проекта нет материалов");

        var before = ReadTransparency(materialId);
        var target = before == 42 ? 43 : 42;

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + materialId +
            ",\"parameter\":\"MATERIAL_PARAM_TRANSPARENCY\",\"value\":\"" + target +
            "\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(ReadTransparency(materialId)).IsEqualTo(target);

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(ReadTransparency(materialId)).IsEqualTo(before);
    }

    [Test]
    public async Task SetParameters_MaterialTransparency_DryRunDoesNotChange()
    {
        var materialId = FirstMaterialId();
        if (materialId == 0)
            Skip.Test("В шаблоне проекта нет материалов");

        var before = ReadTransparency(materialId);
        var target = before == 42 ? 43 : 42;

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + materialId +
            ",\"parameter\":\"MATERIAL_PARAM_TRANSPARENCY\",\"value\":\"" + target +
            "\"}],\"dryRun\":true}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(ReadTransparency(materialId)).IsEqualTo(before);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetMepSystemType_Abbreviation_AppliesReadsBackAndUndoes()
    {
        if (!_systemCreated)
            Skip.Test("В шаблоне проекта нет PipingSystemType или MEP-дисциплина отключена");

        var type = (MEPSystemType)Doc.GetElement(ElementIdCompat.Create(_systemTypeId))!;
        var before = type.Abbreviation ?? string.Empty;
        var target = before == "SCB" ? "SCA" : "SCB";

        var tool = new SetMepSystemTypeTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"systemId\":" + _systemId + ",\"abbreviation\":\"" + target +
                  "\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("abbreviation").GetString())
            .IsEqualTo(target);
        await Assert.That(((MEPSystemType)Doc.GetElement(
            ElementIdCompat.Create(_systemTypeId))!).Abbreviation).IsEqualTo(target);

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(((MEPSystemType)Doc.GetElement(
            ElementIdCompat.Create(_systemTypeId))!).Abbreviation).IsEqualTo(before);
    }

    [Test]
    public async Task SetMepSystemType_NameTaken_TypedConflict()
    {
        if (!_systemCreated)
            Skip.Test("В шаблоне проекта нет PipingSystemType или MEP-дисциплина отключена");

        var otherName = new FilteredElementCollector(Doc)
            .OfClass(typeof(MEPSystemType))
            .Cast<MEPSystemType>()
            .Where(type => type.Id.GetValue() != _systemTypeId)
            .Select(type => type.Name)
            .FirstOrDefault(name =>
                !string.IsNullOrEmpty(name) &&
                name!.IndexOf('"') < 0 && name.IndexOf('\\') < 0);
        if (string.IsNullOrEmpty(otherName))
            Skip.Test("В шаблоне нет второго типа трубопроводной системы с простым именем");

        var tool = new SetMepSystemTypeTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = tool.ExecuteForTests(
            Parse("{\"typeId\":" + _systemTypeId + ",\"name\":\"" + otherName +
                  "\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("name_taken");
    }

    private SetParametersTool CreateSetParametersTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private string ReadAbbreviation()
    {
        var system = Doc.GetElement(ElementIdCompat.Create(_systemId));
        return system?.get_Parameter(BuiltInParameter.RBS_DUCT_PIPE_SYSTEM_ABBREVIATION_PARAM)
                   ?.AsString()
               ?? string.Empty;
    }

    private long FirstMaterialId()
    {
        var id = new FilteredElementCollector(Doc)
            .OfClass(typeof(Material))
            .FirstElementId();
        return id.Equals(ElementId.InvalidElementId) ? 0 : id.GetValue();
    }

    private int ReadTransparency(long materialId)
    {
        var material = Doc.GetElement(ElementIdCompat.Create(materialId)) as Material;
        var parameter = material?.get_Parameter(BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY);
        return parameter?.AsInteger() ?? -1;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
