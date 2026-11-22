using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Saves the active document to its current file path (G.10). Document.Save
/// runs outside any transaction (Revit refuses to save while a transaction is
/// open), so the tool uses the own-pipeline hook of the write base. Not an
/// autosave: the tool only runs when explicitly called, and it never records a
/// change set — saving does not change the model.
/// </summary>
public sealed class SaveDocumentTool : AgentWriteToolBase
{
    public SaveDocumentTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "save_document",
        Description =
            "Saves the active document to its current path (doc.Save()) — the Revit Ctrl+S " +
            "equivalent, only when explicitly requested; it is NOT an autosave and does not " +
            "change the model, so no change set is recorded and undo_last does not revert it. " +
            "dryRun (default) reports IsModified, the path and the number of applied SmartCon " +
            "change sets pending in this document. no_changes when the document has no unsaved " +
            "edits. A document that was never saved to disk needs Save As first — ask the user " +
            "to do it in Revit (document_not_saved_before).",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override bool ManagesOwnTransactions => true;

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct) =>
        throw new InvalidOperationException(
            "SaveDocumentTool runs through ExecuteOutsideTransaction.");

    protected override void ExecuteOutsideTransaction(
        Document document,
        JsonElement args,
        bool dryRun,
        WriteOutcome outcome,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        outcome.SuppressChangeSet = true;
        outcome.Description = "Save document";

        var path = document.PathName;
        if (string.IsNullOrWhiteSpace(path))
        {
            outcome.Success = false;
            outcome.ErrorCode = "document_not_saved_before";
            outcome.Message =
                "The document has never been saved to disk, so there is no path to save to. " +
                "Ask the user to save it once via File > Save As in Revit; Save As by the " +
                "agent is not supported.";
            return;
        }

        var documentId = AgentDocumentIdentity.GetDocumentId(document);
        var pending = CountPendingChangeSets(documentId, ct);
        var isModified = document.IsModified;

        outcome.Data["path"] = path;
        outcome.Data["isModified"] = isModified;
        outcome.Data["pendingChangeSets"] = pending;

        if (!isModified)
        {
            outcome.ErrorCode = "no_changes";
            outcome.Message =
                "The document has no unsaved changes (IsModified=false); there is nothing to " +
                "save. [Action: no save needed — skip the apply call]";
            return;
        }

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = path,
                Reason = "Dry run: the document would be saved to its current path."
            });
            outcome.AffectedCount = 1;
            outcome.Data["wouldSave"] = true;
            return;
        }

        document.Save();

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Name = path,
            NewValue = "saved"
        });
        outcome.AffectedCount = 1;
        outcome.Data["saved"] = true;
        outcome.Data["isModifiedAfter"] = document.IsModified;
    }

    private int CountPendingChangeSets(string documentId, CancellationToken ct)
    {
        try
        {
            var headers = AsyncBridge.RunSync(
                () => ChangeSets.ListAsync(documentId, 100, 0, ct));
            var applied = 0;
            foreach (var header in headers)
            {
                if (header.AppliedAt.HasValue)
                    applied++;
            }

            return applied;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The estimate is advisory only — a broken store must not block saving.
            return 0;
        }
    }
}
