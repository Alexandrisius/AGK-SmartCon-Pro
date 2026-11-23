using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Common pipeline for write tools (mvp-plan §9):
/// guard document/worksharing → execute on the Revit thread → dry-run as
/// run-and-rollback → apply in one transaction → persist a change set → report.
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

    /// </summary>

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
        WriteOutcome outcome;
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

            if (document.IsWorkshared)
            {
                return WriteOutcome.Fail(
                    "workshared_not_supported",
                    "The active document is a workshared model. Writing to a workshared " +
                    "document is not supported in this version.",
                    "Open a detached copy (File > Save As > Detach from Central) and retry.");
            }

            outcome = new WriteOutcome
            {
                DocumentPath = document.PathName,
                RevitVersion = document.Application.VersionNumber,
                DocumentVersion = AgentDocumentState.Capture(document)
            };

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

    private async Task PersistChangeSetAsync(
        WriteOutcome outcome,
        string documentId,
        Guid conversationId,
        CancellationToken ct)
    {
        try
        {
            var header = new ChangeSetHeader
            {
                ConversationId = conversationId == Guid.Empty ? null : conversationId,
                Operation = Definition.Name,
                DocumentId = documentId,
                DocumentPath = outcome.DocumentPath,
                RevitVersion = outcome.RevitVersion,
                Author = Environment.UserName,
                Description = outcome.Description
            };

            var changeSetId = await _changeSets.BeginAsync(header, ct).ConfigureAwait(false);
            await _changeSets
                .AddItemsAsync(changeSetId, outcome.Items, ct)
                .ConfigureAwait(false);
            await _changeSets
                .MarkAppliedAsync(changeSetId, DateTime.UtcNow, ct)
                .ConfigureAwait(false);

            outcome.Data["changeSetId"] = changeSetId.ToString("D");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent change set for '" + Definition.Name + "' was not recorded: " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: the model change is applied, but undo_last will not be able " +
                "to revert it; check write access to agent.db]");
            outcome.Warnings.Add(
                "The change set could not be recorded, so undo_last will not revert " +
                "this operation.");
        }
    }

    private ToolResult BuildResult(WriteOutcome outcome, bool dryRun)
    {
        if (outcome.Success
            && outcome.Batch.Changed.Count == 0
            && outcome.Batch.Failed.Count > 0
            && outcome.AffectedCount == 0)
        {
            // Every requested item failed — report the batch as failed so the
            // model does not treat an empty result as a success.
            outcome.Success = false;
            var codes = outcome.Batch.Failed
                .Select(item => item.ErrorCode)
                .Where(code => !string.IsNullOrEmpty(code))
                .ToList();
            if (codes.Count > 0)
            {
                // A batch where every item failed for the same actionable reason should
                // surface that code at the top level, not hide it in failed[] (rename_views
                // returned all_items_failed while the real cause was duplicate_name). A
                // dominant cause (≥60 % of failures) is promoted too, so a stray second
                // code cannot bury the useful one.
                var groups = codes
                    .GroupBy(code => code!, StringComparer.Ordinal)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key, StringComparer.Ordinal)
                    .ToList();
                if (groups.Count == 1 || groups[0].Count() * 100 >= codes.Count * 60)
                    outcome.ErrorCode = groups[0].Key;
                outcome.Data["errorCodes"] = groups
                    .Select(group => new { code = group.Key, count = group.Count() })
                    .ToList();
            }

            outcome.ErrorCode ??= "all_items_failed";
            if (string.IsNullOrWhiteSpace(outcome.Message))
            {
                var first = outcome.Batch.Failed[0];
                outcome.Message = outcome.Batch.Failed.Count + " item(s) failed";
                if (!string.IsNullOrWhiteSpace(first.Name))
                    outcome.Message += "; first: " + first.Name;
                if (!string.IsNullOrWhiteSpace(first.ErrorCode))
                    outcome.Message += " (" + first.ErrorCode + ")";
                if (!string.IsNullOrWhiteSpace(first.Reason))
                    outcome.Message += " — " + first.Reason;
            }
        }

        outcome.Data["dryRun"] = dryRun;
        // Partial batches are a success with a per-item failed[]: do not leak an
        // item error code as the top-level code (it read as "the whole call failed").
        if (outcome.Success && outcome.Batch.Failed.Count > 0)
        {
            outcome.ErrorCode = null;
            outcome.Data["partialFailure"] = true;
        }

        // "Nothing to do" is a success, but an empty changed[] alone reads like a
        // failure to the model (A8). One top-level flag removes the ambiguity.
        if (outcome.Success
            && outcome.Batch.Changed.Count == 0
            && outcome.Batch.Failed.Count == 0)
        {
            outcome.Data["noChanges"] = true;
        }
        // A successful outcome can still carry a diagnostic code (no_changes): the
        // model needs it to stop retrying an idempotent call (#400).
        if (outcome.Success && !string.IsNullOrEmpty(outcome.ErrorCode))
            outcome.Data["errorCode"] = outcome.ErrorCode;
        outcome.Data["changed"] = outcome.Batch.Changed;
        outcome.Data["skipped"] = outcome.Batch.Skipped;
        outcome.Data["failed"] = outcome.Batch.Failed;
        outcome.Data["total"] = outcome.Batch.Total;
        outcome.Data["affected"] = outcome.AffectedCount;
        if (outcome.Aborted)
            outcome.Data["aborted"] = true;

        var data = AgentJson.Serialize(outcome.Data);
        var appliedText = dryRun
            ? "Dry run: " + outcome.Batch.Changed.Count + " change(s) planned"
            : outcome.Batch.Changed.Count == 0 && outcome.AffectedCount > 0
                ? "Partial apply: " + outcome.AffectedCount + " change(s) applied"
                : outcome.Batch.Changed.Count + " change(s) applied";
        var summary = outcome.Message
            ?? (dryRun
                ? appliedText + ", " +
                  outcome.Batch.Skipped.Count + " skipped, " + outcome.Batch.Failed.Count +
                  " failed. Nothing was modified."
                : appliedText + ", " +
                  outcome.Batch.Skipped.Count + " skipped, " + outcome.Batch.Failed.Count +
                  " failed.");

        // Non-atomic partial dry run: the failed items are deterministic — they will
        // fail on apply too, so tell the model to resend only the passing items.
        if (dryRun && outcome.Success && outcome.Batch.Failed.Count > 0)
        {
            var failedIds = string.Join(", ", outcome.Batch.Failed
                .Take(10)
                .Select(item => item.Id?.ToString() ?? item.Name ?? "?"));
            summary += " " + outcome.Batch.Failed.Count +
                       " item(s) failed validation and will fail again: [" + failedIds +
                       "] (max 10). Repeat the SAME call with dryRun:false WITHOUT the " +
                       "failed items — the passing items are ready to apply.";
        }

        var result = outcome.Success
            ? ToolResult.Ok(data, summary, outcome.AffectedCount, outcome.Warnings)
            : ToolResult.Fail(
                outcome.ErrorCode ?? "write_failed",
                summary,
                outcome.Warnings,
                data,
                affectedCount: outcome.AffectedCount);
        if (!outcome.Success
            && outcome.ErrorCode is not null
            && outcome.ErrorCode.EndsWith("_not_found", StringComparison.Ordinal))
        {
            result = result.WithMessageSuffix(
                AgentDocumentState.NotFoundHint(outcome.DocumentVersion));
        }

        return result.WithDocumentVersion(outcome.DocumentVersion);
    }

    private sealed class WriteAbortSignal : Exception
    {
    }
}
