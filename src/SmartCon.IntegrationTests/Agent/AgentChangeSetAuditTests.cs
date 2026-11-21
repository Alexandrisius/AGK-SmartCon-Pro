using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// G.10 integration coverage: change-set listing/details, undo_until chain
/// semantics (one undo_last per set, rolled-back marking), audit_model checks
/// on a seeded problem model and save_document outside a transaction.
/// </summary>
public sealed class AgentChangeSetAuditTests : RevitApiTest
{
    private const string Comments = "SC_AgentChanges";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _wallId;
    private bool _wallCreated;
    private string? _savedPath;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();
        _savedPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SC_AgentSave_" + Guid.NewGuid().ToString("N") + ".rvt");

        _transactions.RunInTransaction(Doc, "Seed agent changes model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            var wall = ModelSeed.TryCreateWall(doc, level.Id);
            _wallCreated = wall is not null;
            _wallId = wall?.Id.GetValue() ?? 0;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
        if (_savedPath is null)
            return;

        TryDelete(_savedPath);
        var backup = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_savedPath)!,
            System.IO.Path.GetFileNameWithoutExtension(_savedPath) + ".0001.rvt");
        TryDelete(backup);
    }

    [Test]
    public async Task ChangeSetFlow_ListDetailsUndoUntilFirst()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        await Apply(tool, "SC_First");
        await Apply(tool, "SC_Second");
        await Apply(tool, "SC_Third");
        await Assert.That(ReadComments()).IsEqualTo("SC_Third");

        var list = new ListChangeSetsTool(new StubRevitContext(Doc), _changeSets!);
        var listResult = list.ExecuteForTests(
            Doc, Parse("""{"limit":50}"""), CancellationToken.None);
        await Assert.That(listResult.Success).IsTrue();

        var rows = listResult.Data!.Value.GetProperty("changeSets");
        await Assert.That(rows.GetArrayLength()).IsEqualTo(3);
        var appliedRows = 0;
        for (var i = 0; i < rows.GetArrayLength(); i++)
        {
            if (rows[i].GetProperty("status").GetString() == "applied")
                appliedRows++;
        }

        await Assert.That(appliedRows).IsEqualTo(3);

        var firstId = rows[2].GetProperty("changeSetId").GetString()!;
        var details = new GetChangeSetDetailsTool(new StubRevitContext(Doc), _changeSets!);
        var detailsResult = details.ExecuteForTests(
            Doc, Parse("{\"changeSetId\":\"" + firstId + "\"}"), CancellationToken.None);
        await Assert.That(detailsResult.Success).IsTrue();
        var items = detailsResult.Data!.Value.GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsEqualTo(1);
        await Assert.That(items[0].GetProperty("oldValue").GetString()).IsEqualTo(string.Empty);
        await Assert.That(items[0].GetProperty("newValue").GetString()).IsEqualTo("SC_First");

        var until = CreateUndoUntilTool();
        var dryArgs = Parse("{\"changeSetId\":\"" + firstId + "\",\"dryRun\":true}");
        var dry = await until.ExecuteForTests(Doc, dryArgs, approved: true, CancellationToken.None);
        await Assert.That(dry.Success).IsTrue();
        await Assert.That(dry.Data!.Value.GetProperty("totalChain").GetInt32()).IsEqualTo(3);
        await Assert.That(dry.Data!.Value.GetProperty("wouldRevert").GetInt32()).IsEqualTo(3);
        await Assert.That(ReadComments()).IsEqualTo("SC_Third");

        var applyArgs = Parse("{\"changeSetId\":\"" + firstId + "\",\"dryRun\":false}");
        var applied = await until.ExecuteForTests(
            Doc, applyArgs, approved: true, CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();
        await Assert.That(applied.Data!.Value.GetProperty("reverted").GetInt32()).IsEqualTo(3);
        await Assert.That(ReadComments()).IsEqualTo(string.Empty);

        foreach (var set in _changeSets!.All)
            await Assert.That(set.Header.AppliedAt).IsNull();
    }

    [Test]
    public async Task UndoUntil_RolledBackTarget_ReturnsChangeSetNotApplied()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSetParametersTool();
        var applied = tool.ExecuteForTests(
            Parse(SetArgs(Comments)),
            approved: true,
            CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();

        var undo = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var undone = undo.ExecuteForTests(
            Parse("""{"dryRun":false}"""), approved: true, CancellationToken.None);
        await Assert.That(undone.Success).IsTrue();

        var setId = _changeSets!.All[0].Header.Id.ToString("D");
        var until = CreateUndoUntilTool();
        var result = await until.ExecuteForTests(
            Doc,
            Parse("{\"changeSetId\":\"" + setId + "\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("change_set_not_applied");
    }

    [Test]
    public async Task UndoUntil_InvalidGuid_ReturnsTypedError()
    {
        var until = CreateUndoUntilTool();
        var result = await until.ExecuteForTests(
            Doc,
            Parse("""{"changeSetId":"not-a-guid","dryRun":true}"""),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_change_set_id");
    }

    [Test]
    public async Task AuditModel_FindsSeededProblems()
    {
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        string scheduleName = "SC_Audit\u200BSchedule";
        string planName = "SC_Audit_Plan";
        _transactions!.RunInTransaction(Doc, "Seed audit problems", doc =>
        {
            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .First();

            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, planName);
            plan.ViewTemplateId = ElementId.InvalidElementId;

            // Pipes do not exist in the seeded model, so this schedule stays empty.
            var schedule = ViewSchedule.CreateSchedule(
                doc, new ElementId(BuiltInCategory.OST_PipeCurves));
            schedule.Name = scheduleName;

            var titleBlock = ModelSeed.FindTitleBlockTypeId(doc);
            if (titleBlock is not null)
            {
                var sheet = ViewSheet.Create(doc, titleBlock);
                sheet.SheetNumber = "SC_Audit\u200B01";
            }
        });

        var audit = new AuditModelTool(new StubRevitContext(Doc), _changeSets!);
        var result = audit.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);
        await Assert.That(result.Success).IsTrue();

        var checks = result.Data!.Value.GetProperty("checks");
        var byName = new Dictionary<string, JsonElement>();
        for (var i = 0; i < checks.GetArrayLength(); i++)
        {
            var check = checks[i];
            byName[check.GetProperty("name").GetString()!] = check;
        }

        await Assert.That(byName.Count).IsEqualTo(6);

        var invisible = byName["invisible_characters"];
        await Assert.That(invisible.GetProperty("count").GetInt32()).IsGreaterThanOrEqualTo(1);
        var invisibleHit = ContainsItemWithName(invisible, scheduleName);
        await Assert.That(invisibleHit).IsTrue();

        var withoutTemplate = byName["views_without_template"];
        await Assert.That(withoutTemplate.GetProperty("count").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        await Assert.That(ContainsItemWithName(withoutTemplate, planName)).IsTrue();

        var empty = byName["empty_schedules"];
        await Assert.That(empty.GetProperty("count").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(ContainsItemWithName(empty, scheduleName)).IsTrue();
    }

    [Test]
    public async Task AuditModel_UnknownCheck_FailsWithValidNames()
    {
        var audit = new AuditModelTool(new StubRevitContext(Doc), _changeSets!);
        var result = audit.ExecuteForTests(
            Doc, Parse("""{"checks":["not_a_check"]}"""), CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unknown_check");
    }

    [Test]
    public async Task SaveDocument_NeverSaved_ReturnsDocumentNotSavedBefore()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateSaveTool();
        var result = tool.ExecuteForTests(
            Parse("""{"dryRun":false}"""), approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("document_not_saved_before");
    }

    [Test]
    public async Task SaveDocument_RoundTrip_ModifiedToSavedToNoChanges()
    {
        if (!_wallCreated)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        Doc.SaveAs(_savedPath);
        await Assert.That(Doc.IsModified).IsFalse();

        var write = CreateSetParametersTool().ExecuteForTests(
            Parse(SetArgs("SC_AgentSave")),
            approved: true,
            CancellationToken.None);
        await Assert.That(write.Success).IsTrue();
        await Assert.That(Doc.IsModified).IsTrue();

        var tool = CreateSaveTool();

        var dry = tool.ExecuteForTests(
            Parse("""{"dryRun":true}"""), approved: true, CancellationToken.None);
        await Assert.That(dry.Success).IsTrue();
        await Assert.That(dry.Data!.Value.GetProperty("isModified").GetBoolean()).IsTrue();
        await Assert.That(dry.Data!.Value.GetProperty("wouldSave").GetBoolean()).IsTrue();

        var applied = tool.ExecuteForTests(
            Parse("""{"dryRun":false}"""), approved: true, CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();
        await Assert.That(Doc.IsModified).IsFalse();

        var again = tool.ExecuteForTests(
            Parse("""{"dryRun":false}"""), approved: true, CancellationToken.None);
        await Assert.That(again.Success).IsTrue();
        await Assert.That(again.Data!.Value.GetProperty("noChanges").GetBoolean()).IsTrue();
    }

    private SetParametersTool CreateSetParametersTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private UndoUntilTool CreateUndoUntilTool() =>
        new(
            new StubRevitContext(Doc),
            _transactions!,
            _changeSets!,
            new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!));

    private SaveDocumentTool CreateSaveTool() =>
        new(new StubRevitContext(Doc), _transactions!, _changeSets!);

    private async Task Apply(SetParametersTool tool, string value)
    {
        var result = tool.ExecuteForTests(
            Parse(SetArgs(value)),
            approved: true,
            CancellationToken.None);
        await Assert.That(result.Success).IsTrue();
    }

    private string SetArgs(string value) =>
        "{\"items\":[{\"elementId\":" + _wallId + ",\"parameter\":\"Comments\",\"value\":\"" +
        value + "\"}],\"dryRun\":false}";

    private string ReadComments()
    {
        var wall = Doc.GetElement(ElementIdCompat.Create(_wallId));
        return wall?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
               ?? string.Empty;
    }

    private static bool ContainsItemWithName(JsonElement check, string name)
    {
        var items = check.GetProperty("items");
        for (var i = 0; i < items.GetArrayLength(); i++)
        {
            if (string.Equals(items[i].GetProperty("name").GetString(), name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch (Exception)
        {
            // Temp cleanup is best effort.
        }
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
