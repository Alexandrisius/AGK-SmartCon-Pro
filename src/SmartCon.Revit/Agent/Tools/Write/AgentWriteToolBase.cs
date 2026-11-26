using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Common pipeline for write tools (mvp-plan §9): guard the document, take
/// ownership of the batch targets in a workshared local copy → execute on the
/// Revit thread → dry-run as run-and-rollback → apply in one transaction →
/// persist a change set → report.
/// </summary>
public abstract partial class AgentWriteToolBase : IAgentTool, IAgentToolPreflight
{
    private readonly IRevitContext _revitContext;
    private readonly ITransactionService _transactions;
    private readonly IAgentChangeSetStore _changeSets;

    protected AgentWriteToolBase(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _changeSets = changeSets ?? throw new ArgumentNullException(nameof(changeSets));
    }

    public abstract AgentToolDefinition Definition { get; }

    protected IAgentChangeSetStore ChangeSets => _changeSets;

    protected ITransactionService Transactions => _transactions;

    /// <summary>
    /// The data key every write tool fills with its read-back totals: execute_plan
    /// sums it as the tool-level verification of a step.
    /// </summary>
    internal const string ReadBackDataKey = "readBack";

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return await ExecuteCoreAsync(
                context.Revit,
                context.DocumentId,
                context.ConversationId,
                context.WriteApproved,
                args,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam for integration tests: runs the whole pipeline with an
    /// explicit approval flag (no UI confirmation service involved).
    /// </summary>
    internal ToolResult ExecuteForTests(
        JsonElement args,
        bool approved,
        CancellationToken ct)
    {
        var documentId = string.Empty;
        var document = _revitContext.TryGetDocument();
        if (document is not null)
            documentId = AgentDocumentIdentity.GetDocumentId(document);

        return ExecuteCoreAsync(
                new ImmediateAwaitableEvent(),
                documentId,
                Guid.Empty,
                approved,
                args,
                ct)
            .GetAwaiter()
            .GetResult();
    }

    private async Task<ToolResult> ExecuteCoreAsync(
        IAgentAwaitableEvent revit,
        string documentId,
        Guid conversationId,
        bool approved,
        JsonElement args,
        CancellationToken ct)
    {
        var argsCopy = args.Clone();
        var dryRun = AgentJson.GetBool(argsCopy, "dryRun", true);

        if (!dryRun && !approved)
        {
            return ToolResult.Fail(
                "confirmation_required",
                "Write operations require explicit user confirmation. Call the tool with " +
                "dryRun=true first, show the planned changes, and only then repeat the call " +
                "with dryRun=false — the user will be asked to approve it.");
        }

        var outcome = await revit
            .RaiseAsync(_ => ExecuteOnRevitThread(documentId, argsCopy, dryRun, ct), ct)
            .ConfigureAwait(false);

        if (outcome.ModelChanged && outcome.Items.Count > 0 && !outcome.SuppressChangeSet)
        {
            // The model is already modified at this point: the audit trail must be
            // written even if the user pressed Stop or switched documents right after
            // the commit, otherwise undo_last could never find the change.
            await PersistChangeSetAsync(outcome, documentId, conversationId, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (!dryRun && outcome.RollBackChangeSetId.HasValue)
        {
            try
            {
                await _changeSets
                    .MarkRolledBackAsync(
                        outcome.RollBackChangeSetId.Value,
                        DateTime.UtcNow,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SmartConLogger.Warn(
                    "Agent change set " + outcome.RollBackChangeSetId.Value.ToString("N") +
                    " was reverted but not marked as rolled back: " + ex.Message +
                    " [Action: the model is correct; a repeated undo_last may try to revert " +
                    "the same change set again]");
            }
        }

        return BuildResult(outcome, dryRun);
    }

    /// <summary>
    /// Performs the operation inside the transaction. Must not throw for
    /// item-level failures: record them in <see cref="WriteOutcome.Batch"/> and,
    /// when atomic, set <see cref="WriteOutcome.Aborted"/>.
    /// </summary>
    protected abstract void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct);

    protected static string OperationName(string toolName, bool dryRun) =>
        dryRun ? toolName + " (dry run)" : toolName;

    private WriteOutcome ExecuteOnRevitThread(
        string expectedDocumentId,
        JsonElement args,
        bool dryRun,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var document = _revitContext.TryGetDocument();
            if (document is null)
            {
                return WriteOutcome.Fail(
                    "no_active_document",
                    "No Revit project document is open. Open a project and try again.");
            }

            if (!string.IsNullOrEmpty(expectedDocumentId)
                && !string.Equals(
                    AgentDocumentIdentity.GetDocumentId(document),
                    expectedDocumentId,
                    StringComparison.Ordinal))
            {
                return WriteOutcome.Fail(
                    "document_changed",
                    "The active document changed while the request was running. " +
                    "Start a new request in the current document.");
            }

            var outcome = new WriteOutcome
            {
                DocumentPath = document.PathName,
                RevitVersion = document.Application.VersionNumber,
                DocumentVersion = AgentDocumentState.Capture(document)
            };

            if (document.IsWorkshared)
            {
                // A local copy is a normal editable document. The only difference
                // is ownership: an element owned by another user, an out-of-date
                // element or a non-editable workset rolls the whole transaction
                // back, so the batch takes ownership first and skips what stayed
                // unavailable. A detached copy has no central to borrow from.
                WorksharingScope = AgentWorksharingScope.Create(document);
                var worksharingFailure =
                    PrepareWorksharing(document, args, outcome, dryRun, ct);
                if (worksharingFailure is not null)
                    return worksharingFailure;
            }
            else
            {
                WorksharingScope = AgentWorksharingScope.None;
                outcome.Data["worksharing"] = WorksharingScope.ToPayload();
            }

            if (ManagesOwnTransactions)
            {
                ExecuteOutsideTransaction(document, args, dryRun, outcome, ct);
                return outcome;
            }

            ExecuteInTransaction(document, args, dryRun, outcome, ct);
            AfterTransaction(document, args, dryRun, outcome, ct);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                "Agent tool '" + Definition.Name + "' failed: " +
                ex.GetType().Name + ": " + ex.Message);
            return WriteOutcome.Fail("tool_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void ExecuteInTransaction(
        Document document,
        JsonElement args,
        bool dryRun,
        WriteOutcome outcome,
        CancellationToken ct)
    {
        var name = OperationName(Definition.Name, dryRun);
        Exception? failure = null;

        if (dryRun)
        {
            var applied = false;
            var ran = _transactions.RunAndRollback(name, doc =>
            {
                try
                {
                    Apply(doc, args, outcome, dryRun: true, ct);
                    applied = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            outcome.ModelChanged = false;
            if (!ran)
            {
                // RunAndRollback reports false in two different situations (#479): the
                // transaction never started (Apply did not run, "0 planned" would be a
                // lie) or the rollback did not complete (a change may have stayed).
                // Document.IsModifiable is no pre-check: it is false in a headless
                // host where transactions do start.
                outcome.Success = false;
                if (applied)
                {
                    outcome.ErrorCode = "dry_run_rollback_failed";
                    outcome.Message =
                        "The dry run could not be rolled back: a change may have stayed in the " +
                        "model. Check the model before repeating the call; undo_last reverts it.";
                    outcome.ModelChanged = true;
                }
                else
                {
                    outcome.ErrorCode = NotModifiable().ErrorCode;
                    outcome.Message = NotModifiable().Message;
                }
            }
            else if (failure is not null)
            {
                outcome.Success = false;
                outcome.ErrorCode = "dry_run_failed";
                outcome.Message = failure.GetType().Name + ": " + failure.Message;
                outcome.Warnings.Add(
                    "The dry run could not complete; nothing was changed in the model.");
            }

            return;
        }

        var committed = false;
        try
        {
            committed = _transactions.RunInTransaction(name, doc =>
            {
                Apply(doc, args, outcome, dryRun: false, ct);
                if (outcome.Aborted)
                    throw new WriteAbortSignal();
            });
        }
        catch (WriteAbortSignal)
        {
            committed = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unexpected exception must not erase the report: failed[] and the
            // per-item reasons are the only useful part of the answer (external
            // run 6: create_view_filter returned tool_failed with failed[]=[]).
            SmartConLogger.Error(
                "Agent tool '" + Definition.Name + "' failed while applying: " +
                ex.GetType().Name + ": " + ex.Message);
            committed = false;
            outcome.Success = false;
            outcome.ErrorCode ??= "tool_failed";
            outcome.Message = (string.IsNullOrWhiteSpace(outcome.Message)
                    ? string.Empty
                    : outcome.Message + " ") +
                ex.GetType().Name + ": " + ex.Message;
            outcome.Warnings.Add(
                "The operation failed unexpectedly and the transaction was rolled back; " +
                "see failed[] for the per-item reasons.");
        }

        if (!committed)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "transaction_rolled_back";
            outcome.Message = (outcome.Message ?? "The transaction was rolled back.") +
                " Nothing was changed in the model.";
            MarkItemsRolledBack(outcome);
            // The read-back block was collected inside the rolled-back transaction:
            // execute_plan sums it as verified, so it must not outlive the rollback.
            outcome.Data.Remove(ReadBackDataKey);
            outcome.ModelChanged = false;
            // A silent rollback must not close the change set: the model still holds the
            // applied state, so undo_last has to be able to target this very set again.
            outcome.RollBackChangeSetId = null;
            AppendWorksharingRollbackHint(outcome, document);

            return;
        }

        outcome.ModelChanged = outcome.AffectedCount > 0;
    }

    private static WriteOutcome NotModifiable() =>
        WriteOutcome.Fail(
            "document_not_modifiable",
            "The active document cannot be modified right now: it is read-only, a modal " +
            "dialog, an edit mode or another transaction is open. Nothing was checked or changed.",
            "Close the dialog, finish the edit mode or open the document for editing, " +
            "then repeat the call.");

    private static void MarkItemsRolledBack(WriteOutcome outcome)
    {
        foreach (var report in outcome.Batch.Changed)
        {
            report.Reason = "Transaction rolled back by Revit — the change was not applied.";
            outcome.Batch.Skipped.Add(report);
        }

        outcome.Batch.Changed.Clear();
        outcome.Items.RemoveAll(item => item.Status == ChangeStatus.Applied);
        outcome.AffectedCount = 0;
    }

    private sealed class WriteAbortSignal : Exception
    {
    }
}
