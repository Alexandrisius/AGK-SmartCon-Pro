using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// #479: a dry run on a document that cannot take a transaction must say so
/// instead of reporting an honest-looking "0 change(s) planned".
/// </summary>
public sealed class AgentWriteToolDryRunGuardTests : RevitApiTest
{
    private Document? _document;
    private RevitTransactionService? _transactions;
    private long _wallId;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _transactions.RunInTransaction(Doc, "Seed dry-run guard model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            _wallId = ModelSeed.TryCreateWall(doc, level.Id)?.Id.GetValue() ?? 0;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task SetParameters_DryRun_OnLockedDocument_ReturnsTypedFailureAndChangesNothing()
    {
        if (_wallId == 0)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = CreateTool();

        // A foreign open transaction is the document state of the original report:
        // the tool's own transaction cannot start while it is held.
        using (var hold = new Transaction(Doc, "test: hold the document"))
        {
            hold.Start();
            var result = tool.ExecuteForTests(
                DryRunArgs(),
                approved: true,
                CancellationToken.None);

            await Assert.That(result.Success).IsFalse();
            await Assert.That(result.ErrorCode).IsEqualTo("document_not_modifiable");
            await Assert.That(ReadComments()).IsEqualTo(string.Empty);
            hold.RollBack();
        }
    }

    [Test]
    public async Task SetParameters_DryRun_AfterUnlock_PlansTheChange()
    {
        if (_wallId == 0)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var result = CreateTool().ExecuteForTests(
            DryRunArgs(),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success ? "ok" : result.ErrorCode + ": " + result.Message)
            .IsEqualTo("ok");
        await Assert.That(result.AffectedCount).IsEqualTo(1);
        await Assert.That(ReadComments()).IsEqualTo(string.Empty);
    }

    private SetParametersTool CreateTool() =>
        new(new StubRevitContext(Doc), _transactions!, new InMemoryAgentChangeSetStore());

    private JsonElement DryRunArgs() =>
        JsonSerializer.SerializeToElement(new
        {
            items = new[] { new { elementId = _wallId, parameter = "Comments", value = "SC_DryRunGuard" } },
            dryRun = true
        });

    private string ReadComments() =>
        Doc.GetElement(ElementIdCompat.Create(_wallId))?
            .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?
            .AsString() ?? string.Empty;
}
