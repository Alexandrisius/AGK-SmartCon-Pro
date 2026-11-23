using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// get_document_status (night run 2, C2-08): a fresh unsaved project is
/// writable with the unsaved_document blocker, Document.IsModifiable is true
/// only while a transaction is open (the live probe of the documented
/// semantics), and the tool routes by documentId like the other read tools.
/// </summary>
public sealed class AgentDocumentStatusToolTests : RevitApiTest
{
    private Document? _document;
    private RevitThreadEvent? _event;

    private Document Doc => _document!;

    private RevitThreadEvent Event => _event!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _event = new RevitThreadEvent(Application);
    }

    [After(Test)]
    public void Close()
    {
        try
        {
            _document?.Close(false);
        }
        catch (Exception)
        {
            // The document may already be closed by a test scenario.
        }
    }

    [Test]
    public async Task FreshProject_IsWritableWithUnsavedBlocker()
    {
        var result = await Run(new GetDocumentStatusTool(new StubRevitContext(Doc)), new { });

        await Assert.That(result.Success).IsTrue();
        var payload = result.Data!.Value;
        await Assert.That(payload.GetProperty("writable").GetBoolean()).IsTrue();
        await Assert.That(payload.GetProperty("state").GetString()).IsEqualTo("writable");
        await Assert.That(payload.GetProperty("agentWriteTools").GetString())
            .IsEqualTo("supported");

        var flags = payload.GetProperty("revitFlags");
        await Assert.That(flags.GetProperty("isReadOnly").GetBoolean()).IsFalse();
        // Outside any transaction false is the normal idle state.
        await Assert.That(flags.GetProperty("isModifiable").GetBoolean()).IsFalse();

        var document = payload.GetProperty("documentInfo");
        await Assert.That(document.GetProperty("isFamilyDocument").GetBoolean()).IsFalse();
        await Assert.That(document.GetProperty("isSaved").GetBoolean()).IsFalse();

        // A document with no path blocks save_document, not the write tools.
        var blockers = payload.GetProperty("blockerCodes").EnumerateArray()
            .Select(code => code.GetString() ?? string.Empty)
            .ToList();
        await Assert.That(blockers.Contains("unsaved_document")).IsTrue();
        await Assert.That(blockers.Contains("document_read_only")).IsFalse();
        await Assert.That(blockers.Contains("transaction_open")).IsFalse();

        var session = payload.GetProperty("session");
        await Assert.That(session.GetProperty("openDocuments").GetInt32()).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task InsideTransaction_IsModifiableIsTrueAndBlocksWriteTools()
    {
        // The documented semantics, probed live: IsModifiable is true ONLY
        // while a transaction is open in the document.
        var tool = new GetDocumentStatusTool(new StubRevitContext(Doc));
        ToolResult? inside = null;
        var transactions = new RevitTransactionService(new StubRevitContext(Doc));
        transactions.RunInTransaction(Doc, "Probe isModifiable", _ =>
        {
            inside = tool.ExecuteForTests(Doc, Parse(new { }), CancellationToken.None);
        });

        await Assert.That(inside is not null).IsTrue();
        await Assert.That(inside!.Success).IsTrue();
        var payload = inside.Data!.Value;
        await Assert.That(payload.GetProperty("revitFlags").GetProperty("isModifiable")
            .GetBoolean()).IsTrue();
        await Assert.That(payload.GetProperty("writable").GetBoolean()).IsFalse();
        await Assert.That(payload.GetProperty("state").GetString())
            .IsEqualTo("transaction_open");
        await Assert.That(payload.GetProperty("agentWriteTools").GetString())
            .IsEqualTo("blocked");
        var blockers = payload.GetProperty("blockerCodes").EnumerateArray()
            .Select(code => code.GetString() ?? string.Empty)
            .ToList();
        await Assert.That(blockers.Contains("transaction_open")).IsTrue();

        // After the transaction the document is writable again.
        var after = await Run(tool, new { });
        await Assert.That(after.Data!.Value.GetProperty("state").GetString())
            .IsEqualTo("writable");
    }

    [Test]
    public async Task OwnDocumentId_RoutesBackToActiveDocument()
    {
        var ownId = AgentDocumentIdentity.GetDocumentId(Doc);

        var result = await Run(new GetDocumentStatusTool(new StubRevitContext(Doc)),
            new { documentId = ownId });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("document").GetProperty("scope")
            .GetString()).IsEqualTo("active");
    }

    // ── Support ────────────────────────────────────────────────────────

    private async Task<ToolResult> Run(IAgentTool tool, object args)
    {
        var context = new AgentToolContext(
            Event,
            AgentDocumentIdentity.GetDocumentId(Doc),
            Guid.Empty);
        return await tool.ExecuteAsync(context, Parse(args), CancellationToken.None);
    }

    private static JsonElement Parse(object args)
    {
        var json = JsonSerializer.Serialize(args);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
