using System.Globalization;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

public sealed class AgentWriteParameterUndoTests : RevitApiTest
{
    private const string PlanName = "SC_AgentParam_Plan";
    private const string Comments = "SC_AgentParam";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _levelId;
    private long _viewId;
    private long _wallId;
    private bool _wallCreated;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed agent parameter model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            level.Name = "SC_AgentParam_Level";
            _levelId = level.Id.GetValue();

            var view = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanName);
            _viewId = view.Id.GetValue();

            var wall = ModelSeed.TryCreateWall(doc, level.Id);
            _wallCreated = wall is not null;
            _wallId = wall?.Id.GetValue() ?? 0;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task SetParameters_DryRun_DoesNotChangeModel()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Comments\",\"value\":\"DryRunValue\"}],\"dryRun\":true}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(ReadComments()).IsEqualTo(string.Empty);
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetParameters_SetsStringAndRecordsChange()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Comments\",\"value\":\"" + Comments + "\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(ReadComments()).IsEqualTo(Comments);

        var set = _changeSets!.Last!;
        await Assert.That(set.Header.Operation).IsEqualTo("set_parameters");
        await Assert.That(set.Items[0].ParameterName).IsEqualTo("instance:Comments");
        await Assert.That(set.Items[0].NewValue).IsEqualTo(Comments);
    }

    [Test]
    public async Task SetParameters_ReadOnlyParameter_ReturnsReadonlyParameter()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Area\",\"value\":\"10\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("readonly_parameter");
    }

    [Test]
    public async Task SetParameters_DoubleWithMillimeterUnit_RoundTrips()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Base Offset\",\"value\":\"250\",\"unit\":\"mm\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var element = Doc.GetElement(ElementIdCompat.Create(_wallId));
        var parameter = element!.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
        await Assert.That(parameter).IsNotNull();
        var expected = UnitUtils.ConvertToInternalUnits(250, UnitTypeId.Millimeters);
        await Assert.That(Math.Abs(parameter!.AsDouble() - expected) < 1e-6).IsTrue();
    }

    [Test]
    public async Task SetParameters_TypeScope_WritesTypeParameter()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Description\",\"scope\":\"type\",\"value\":\"AgentTypeDescription\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var element = Doc.GetElement(ElementIdCompat.Create(_wallId))!;
        var type = (Element)Doc.GetElement(element.GetTypeId())!;
        var description = type.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);
        await Assert.That(description!.AsString()).IsEqualTo("AgentTypeDescription");
        await Assert.That(_changeSets!.Last!.Items[0].ParameterName)
            .IsEqualTo("type:Description");
    }

    [Test]
    public async Task SetParameters_InstanceScopeForTypeParameter_ReturnsScopeMismatch()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Description\",\"value\":\"X\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("scope_mismatch");
    }

    [Test]
    public async Task UndoLast_RestoresPreviousViewName()
    {
        var renameTool = new RenameViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var renameArgs = Parse(
            $$"""{"items":[{"viewId":{{_viewId}},"newName":"SC_AgentParam_Renamed"}],"dryRun":false}""");
        var renamed = renameTool.ExecuteForTests(renameArgs, approved: true, CancellationToken.None);
        await Assert.That(renamed.Success).IsTrue();
        await Assert.That(GetView().Name).IsEqualTo("SC_AgentParam_Renamed");

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undoResult = undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""), approved: true, CancellationToken.None);

        await Assert.That(undoResult.Success).IsTrue();
        await Assert.That(GetView().Name).IsEqualTo(PlanName);
        await Assert.That(_changeSets!.Last!.Header.AppliedAt).IsNull();
    }

    [Test]
    public async Task UndoLast_ConflictAfterExternalChange_IsSkipped()
    {
        var renameTool = new RenameViewsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var renameArgs = Parse(
            $$"""{"items":[{"viewId":{{_viewId}},"newName":"SC_AgentParam_Renamed"}],"dryRun":false}""");
        var renamed = renameTool.ExecuteForTests(renameArgs, approved: true, CancellationToken.None);
        await Assert.That(renamed.Success).IsTrue();

        _transactions!.RunInTransaction(Doc, "External rename", doc =>
        {
            GetView().Name = "SC_AgentParam_External";
        });

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undoResult = undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""), approved: true, CancellationToken.None);

        await Assert.That(undoResult.Success).IsFalse();
        await Assert.That(undoResult.ErrorCode).IsEqualTo("undo_conflict");
        await Assert.That(GetView().Name).IsEqualTo("SC_AgentParam_External");
    }

    [Test]
    public async Task UndoLast_WithoutAppliedChangeSets_ReturnsNothingToUndo()
    {
        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""), approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("nothing_to_undo");
    }

    [Test]
    public async Task UndoLast_RestoresParameterValue()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Comments\",\"value\":\"" + Comments + "\"}],\"dryRun\":false}");
        var applied = tool.ExecuteForTests(args, approved: true, CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();
        await Assert.That(ReadComments()).IsEqualTo(Comments);

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undoResult = undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""), approved: true, CancellationToken.None);

        await Assert.That(undoResult.Success).IsTrue();
        await Assert.That(ReadComments()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RemapParameterValues_CopiesCommentsToMark()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        _transactions!.RunInTransaction(Doc, "Seed value", doc =>
        {
            var wall = doc.GetElement(ElementIdCompat.Create(_wallId));
            wall?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Comments);
        });

        var tool = new RemapParameterValuesTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            """{"mapping":[{"source":{"name":"Comments"},"target":{"name":"Mark"}}],"filter":{"categories":["OST_Walls"]},"dryRun":false}""");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var wall = Doc.GetElement(ElementIdCompat.Create(_wallId))!;
        await Assert.That(wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)!.AsString())
            .IsEqualTo(Comments);
        await Assert.That(_changeSets!.Last!.Items[0].ParameterName).IsEqualTo("instance:Mark");
    }

    [Test]
    public async Task SetParameters_DoubleWithoutUnit_Fails()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Base Offset\",\"value\":\"250\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("invalid_value");
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SetParameters_DoubleWithRawUnit_WritesInternalUnits()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Base Offset\",\"value\":\"0.25\",\"unit\":\"raw\"}],\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var element = Doc.GetElement(ElementIdCompat.Create(_wallId))!;
        var parameter = element.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
        await Assert.That(Math.Abs(parameter!.AsDouble() - 0.25) < 1e-9).IsTrue();
    }

    [Test]
    public async Task SetParameters_TypeScope_ChangeItemReferencesTypeElement()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var args = Parse(
            "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Description\",\"scope\":\"type\",\"value\":\"AgentTypeOwner\"}],\"dryRun\":false}");

        var applied = tool.ExecuteForTests(args, approved: true, CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();

        var wall = Doc.GetElement(ElementIdCompat.Create(_wallId))!;
        var type = Doc.GetElement(wall.GetTypeId())!;
        var item = _changeSets!.Last!.Items[0];
        await Assert.That(item.UniqueId).IsEqualTo(type.UniqueId);
        await Assert.That(item.ElementId).IsEqualTo(type.Id.GetValue());

        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undo = undoTool.ExecuteForTests(
            Parse("{\"dryRun\":false}"), approved: true, CancellationToken.None);

        await Assert.That(undo.Success).IsTrue();
        var description = type.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);
        await Assert.That(description!.AsString()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RemapParameterValues_UnknownCategory_Fails()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = new RemapParameterValuesTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"mapping\":[{\"source\":{\"name\":\"Comments\"},\"target\":{\"name\":\"Mark\"}}]," +
            "\"filter\":{\"categories\":[\"OST_NotARealCategory\"]},\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unknown_category");
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RemapParameterValues_CategoryDisplayName_Works()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        _transactions!.RunInTransaction(Doc, "Seed value", doc =>
        {
            var wall = doc.GetElement(ElementIdCompat.Create(_wallId));
            wall?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(Comments);
        });

        var tool = new RemapParameterValuesTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var args = Parse(
            "{\"mapping\":[{\"source\":{\"name\":\"Comments\"},\"target\":{\"name\":\"Mark\"}}]," +
            "\"filter\":{\"categories\":[\"Walls\"]},\"dryRun\":false}");

        var result = tool.ExecuteForTests(args, approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var wall = Doc.GetElement(ElementIdCompat.Create(_wallId))!;
        await Assert.That(wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)!.AsString())
            .IsEqualTo(Comments);
    }

    [Test]
    public async Task SetParameters_GuidOfATypeBoundSharedParameter_WritesAndReadsBack()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var previousFile = Application.SharedParametersFilename;
        var sharedFile = Path.Combine(
            Path.GetTempPath(), "smartcon-guid-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(sharedFile, string.Empty);
        Application.SharedParametersFilename = sharedFile;
        try
        {
            // A shared parameter bound to the wall types: the instance itself
            // carries it only through its type.
            var file = Application.OpenSharedParameterFile();
            var group = file!.Groups.get_Item("SmartConTests")
                       ?? file.Groups.Create("SmartConTests");
            group!.Definitions.Create(new ExternalDefinitionCreationOptions(
                "SC_GuidProbe", SpecTypeId.String.Text));
            var bound = new AddProjectSharedParameterTool(
                    new StubRevitContext(Doc), _transactions!, _changeSets!)
                .ExecuteForTests(
                    Parse(
                        "{\"name\":\"SC_GuidProbe\",\"spec\":\"text\"," +
                        "\"categories\":[\"OST_Walls\"],\"binding\":\"type\",\"dryRun\":false}"),
                    approved: true,
                    CancellationToken.None);
            await Assert.That(bound.Success).IsTrue();

            var wall = Doc.GetElement(ElementIdCompat.Create(_wallId))!;
            var type = (ElementType)Doc.GetElement(wall.GetTypeId())!;
            Guid? guid = null;
            foreach (Parameter parameter in type.Parameters)
            {
                if (parameter.IsShared
                    && string.Equals(
                        parameter.Definition?.Name,
                        "SC_GuidProbe",
                        StringComparison.Ordinal))
                {
                    guid = parameter.GUID;
                    break;
                }
            }

            await Assert.That(guid is not null).IsTrue();

            // The instance parameters are scanned first and are all non-shared:
            // the scan must skip them instead of throwing on .GUID and reach the
            // type-bound shared parameter.
            var written = CreateSetParametersTool().ExecuteForTests(
                Parse(
                    "{\"items\":[{\"elementId\":" + _wallId + ",\"guid\":\"" + guid!.Value.ToString("D") +
                    "\",\"scope\":\"type\",\"value\":\"ByGuid\"}],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None);

            await Assert.That(written.Success).IsTrue();
            await Assert.That(type.get_Parameter(guid.Value)!.AsString()).IsEqualTo("ByGuid");

            var unknown = CreateSetParametersTool().ExecuteForTests(
                Parse(
                    "{\"items\":[{\"elementId\":" + _wallId + ",\"guid\":\"" +
                    Guid.NewGuid().ToString("D") +
                    "\",\"scope\":\"type\",\"value\":\"X\"}],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None);

            await Assert.That(unknown.Success).IsFalse();
            await Assert.That(unknown.ErrorCode).IsEqualTo("parameter_not_found");
            await Assert.That(unknown.Message).Contains("list_project_parameters");
        }
        finally
        {
            try
            {
                Application.SharedParametersFilename = previousFile ?? string.Empty;
            }
            catch (Exception)
            {
                // Best effort in the test host.
            }

            try
            {
                if (File.Exists(sharedFile))
                    File.Delete(sharedFile);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    private SetParametersTool CreateSetParametersTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private View GetView() => (View)Doc.GetElement(ElementIdCompat.Create(_viewId));

    private string ReadComments()
    {
        var wall = Doc.GetElement(ElementIdCompat.Create(_wallId));
        return wall?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
               ?? string.Empty;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
