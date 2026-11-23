using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// What a write tool answers when its apply transaction does not commit: nothing
/// of the applied state may survive in the answer. The read-back totals were the
/// exception — collected inside the rolled-back transaction, they still read as
/// "verified", and execute_plan sums them (night run 2, phase A review VA-6).
/// </summary>
public sealed class AgentWriteRollbackTests : RevitApiTest
{
    private Document? _document;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task Apply_CommitHolds_ReadBackTravelsWithTheResult()
    {
        var tool = NewTool(new RevitTransactionService(new StubRevitContext(Doc)), throwOnApply: false);

        var result = tool.ExecuteForTests(ApplyArgs(), approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Data(result).GetProperty("readBack").GetProperty("verified").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Apply_SilentRollback_CarriesNoReadBack()
    {
        // Revit rolled the transaction back after a complete Apply and only
        // reported the commit as failed: no exception reached the tool.
        var tool = NewTool(
            new NeverCommits(new RevitTransactionService(new StubRevitContext(Doc))), throwOnApply: false);

        var result = tool.ExecuteForTests(ApplyArgs(), approved: true, CancellationToken.None);
        var data = Data(result);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("transaction_rolled_back");
        await Assert.That(data.TryGetProperty("readBack", out _)).IsFalse();
        await Assert.That(data.GetProperty("affected").GetInt32()).IsEqualTo(0);
        await Assert.That(data.TryGetProperty("changeSetId", out _)).IsFalse();
    }

    [Test]
    public async Task Apply_ThrowsAfterReporting_CarriesNoReadBack()
    {
        var tool = NewTool(new RevitTransactionService(new StubRevitContext(Doc)), throwOnApply: true);

        var result = tool.ExecuteForTests(ApplyArgs(), approved: true, CancellationToken.None);
        var data = Data(result);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("tool_failed");
        await Assert.That(data.TryGetProperty("readBack", out _)).IsFalse();
        await Assert.That(data.GetProperty("affected").GetInt32()).IsEqualTo(0);
    }

    private ReadBackTool NewTool(ITransactionService transactions, bool throwOnApply) =>
        new(new StubRevitContext(Doc), transactions, new InMemoryAgentChangeSetStore(), throwOnApply);

    private static JsonElement Data(ToolResult result) => result.Data!.Value;

    private static JsonElement ApplyArgs()
    {
        using var document = JsonDocument.Parse("""{"dryRun":false,"value":"new"}""");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// A write tool that reports one applied value and its read-back from inside
    /// Apply, the way set_parameters and the other write tools do.
    /// </summary>
    private sealed class ReadBackTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets,
        bool throwOnApply) : AgentWriteToolBase(revitContext, transactions, changeSets)
    {
        public override AgentToolDefinition Definition { get; } = new()
        {
            Name = "sc_readback_probe",
            Description = "Reports one applied value and its read-back.",
            Risk = AgentToolRisk.Write,
            SupportsDryRun = true,
            InputSchemaJson =
                """{"type":"object","properties":{"value":{"type":"string"},"dryRun":{"type":"boolean"}},"additionalProperties":false}"""
        };

        protected override void Apply(
            Document doc,
            JsonElement args,
            WriteOutcome outcome,
            bool dryRun,
            CancellationToken ct)
        {
            outcome.AffectedCount = 1;
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = "probe",
                ParameterName = "Probe",
                OldValue = "old",
                NewValue = "new",
                Status = ChangeStatus.Applied
            });
            outcome.Data["readBack"] = new { expected = 1, verified = 1, mismatched = 0, notVerifiable = 0 };

            if (!dryRun && throwOnApply)
                throw new InvalidOperationException("the model refused the change after Apply");
        }
    }

    /// <summary>
    /// The commit that does not hold: the action runs for real, then the
    /// transaction is rolled back and false is reported.
    /// </summary>
    private sealed class NeverCommits(ITransactionService inner) : ITransactionService
    {
        public bool RunInTransaction(string name, Action<Document> action)
        {
            inner.RunAndRollback(name, action);
            return false;
        }

        public bool RunInTransaction(Document document, string name, Action<Document> action)
        {
            inner.RunAndRollback(name, action);
            return false;
        }

        public bool RunInTransactionGroup(string name, Action<Document> action) =>
            inner.RunInTransactionGroup(name, action);

        public ITransactionGroupSession BeginGroupSession(string name) => inner.BeginGroupSession(name);

        public T? RunAndRollback<T>(string name, Func<Document, T> action) where T : struct =>
            inner.RunAndRollback(name, action);

        public bool RunAndRollback(string name, Action<Document> action) => inner.RunAndRollback(name, action);

        public ITransactionScope BeginTransaction(Document document, string name) =>
            inner.BeginTransaction(document, name);
    }
}
