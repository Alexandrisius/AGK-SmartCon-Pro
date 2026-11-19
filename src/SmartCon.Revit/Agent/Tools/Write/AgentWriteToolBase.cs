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
public abstract class AgentWriteToolBase : IAgentTool, IAgentToolPreflight
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

    /// <summary>
    /// Tools that own the document lifecycle (family documents: EditFamily must run
    /// outside any project transaction) return true and implement
    /// <see cref="ExecuteOutsideTransaction"/>. Default false keeps the standard
    /// dry-run/apply transaction pipeline for every existing tool.
    /// </summary>
    protected virtual bool ManagesOwnTransactions => false;

    /// <summary>
    /// Runs the whole operation on the Revit thread when
    /// <see cref="ManagesOwnTransactions"/> is true. The implementation must set
    /// <see cref="WriteOutcome.ModelChanged"/> itself.
    /// </summary>
    protected virtual void ExecuteOutsideTransaction(
        Document document,
        JsonElement args,
        bool dryRun,
        WriteOutcome outcome,
        CancellationToken ct) =>
        throw new InvalidOperationException(
            "The tool declares its own transaction pipeline but does not implement it.");

    /// <summary>
    /// Post-transaction hook that runs outside the transaction, on the Revit thread.
    /// Used by undo_last to restore family documents, which cannot be loaded back
    /// while a project transaction is open. Default no-op.
    /// </summary>
    protected virtual void AfterTransaction(
        Document document,
        JsonElement args,
        bool dryRun,
        WriteOutcome outcome,
        CancellationToken ct)
    {
    }

    /// <summary>
    /// L1 preflight: cheap argument checks on the Revit thread, before any
    /// transaction and before the dry run.
    /// </summary>
    public async Task<PlanPreflightResult> PreflightAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var expectedDocumentId = context.DocumentId;
        return await context.Revit
            .RaiseAsync(_ => PreflightOnRevitThread(expectedDocumentId, args, ct), ct)
            .ConfigureAwait(false);
    }

    private PlanPreflightResult PreflightOnRevitThread(
        string expectedDocumentId,
        JsonElement args,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var document = _revitContext.TryGetDocument();
            if (document is null)
                return PlanPreflightResult.Fail(
                    "no_active_document", "No Revit project document is open.");

            if (!string.IsNullOrEmpty(expectedDocumentId)
                && !string.Equals(
                    AgentDocumentIdentity.GetDocumentId(document),
                    expectedDocumentId,
                    StringComparison.Ordinal))
            {
                return PlanPreflightResult.Fail(
                    "document_changed",
                    "The active document changed before the plan started.");
            }

            if (document.IsWorkshared)
                return PlanPreflightResult.Fail(
                    "workshared_not_supported",
                    "The active document is a workshared model.");

            return PreflightCore(document, args, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PlanPreflightResult.Fail(
                "preflight_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Default check: every ElementId-like argument must resolve, and view /
    /// level / template arguments must point at the right element class.
    /// Override for tool-specific checks (family compatibility, limits).
    /// </summary>
    protected virtual PlanPreflightResult PreflightCore(
        Document document,
        JsonElement args,
        CancellationToken ct)
    {
        foreach (var reference in EnumerateIdReferences(args))
        {
            var element = document.GetElement(ElementIdCompat.Create(reference.Id));
            if (element is null)
            {
                return PlanPreflightResult.Fail(
                    "unresolved_id",
                    "Element " + reference.Id + " (" + reference.Key + ") does not exist.");
            }

            var problem = ValidateReferenceKind(element, reference.Key);
            if (problem is not null)
                return PlanPreflightResult.Fail("invalid_reference", problem);
        }

        return PlanPreflightResult.Pass();
    }

    private static string? ValidateReferenceKind(Element element, string key)
    {
        var normalized = key.ToLowerInvariant();
        if (normalized.EndsWith("viewid", StringComparison.Ordinal)
            || normalized.EndsWith("viewids", StringComparison.Ordinal))
        {
            return element is View
                ? null
                : "Element " + element.Id.GetValue() + " (" + key + ") is not a view.";
        }

        if (normalized.EndsWith("levelid", StringComparison.Ordinal)
            || normalized.EndsWith("levelids", StringComparison.Ordinal))
        {
            return element is Level
                ? null
                : "Element " + element.Id.GetValue() + " (" + key + ") is not a level.";
        }

        if (normalized.EndsWith("templateid", StringComparison.Ordinal)
            || normalized.EndsWith("templateids", StringComparison.Ordinal))
        {
            return element is View view && view.IsTemplate
                ? null
                : "Element " + element.Id.GetValue() + " (" + key + ") is not a view template.";
        }

        return null;
    }

    private static IEnumerable<(string Key, long Id)> EnumerateIdReferences(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var property in args.EnumerateObject())
        {
            foreach (var reference in Enumerate(property.Name, property.Value))
                yield return reference;
        }

        static IEnumerable<(string Key, long Id)> Enumerate(string key, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id))
            {
                if (IsIdKey(key) && id > 0)
                    yield return (key, id);
                yield break;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    foreach (var reference in Enumerate(key, item))
                        yield return reference;
                }

                yield break;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    foreach (var reference in Enumerate(property.Name, property.Value))
                        yield return reference;
                }
            }
        }

        static bool IsIdKey(string key)
        {
            var normalized = key.ToLowerInvariant();
            if (normalized.EndsWith("categoryid", StringComparison.Ordinal)
                || normalized.EndsWith("categoryids", StringComparison.Ordinal)
                || normalized.EndsWith("worksetid", StringComparison.Ordinal)
                || normalized.EndsWith("worksetids", StringComparison.Ordinal))
            {
                return false;
            }

            return normalized.EndsWith("id", StringComparison.Ordinal)
                   || normalized.EndsWith("ids", StringComparison.Ordinal);
        }
    }

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
                RevitVersion = document.Application.VersionNumber
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
            _transactions.RunAndRollback(name, doc =>
            {
                try
                {
                    Apply(doc, args, outcome, dryRun: true, ct);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            outcome.ModelChanged = false;
            if (failure is not null)
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

        if (!committed)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "transaction_rolled_back";
            outcome.Message = (outcome.Message ?? "The transaction was rolled back.") +
                " Nothing was changed in the model.";
            MarkItemsRolledBack(outcome);
            outcome.ModelChanged = false;
            // A silent rollback must not close the change set: the model still holds the
            // applied state, so undo_last has to be able to target this very set again.
            outcome.RollBackChangeSetId = null;
            return;
        }

        outcome.ModelChanged = outcome.AffectedCount > 0;
    }

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
            outcome.ErrorCode ??= "all_items_failed";
        }

        outcome.Data["dryRun"] = dryRun;
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

        return outcome.Success
            ? ToolResult.Ok(data, summary, outcome.AffectedCount, outcome.Warnings)
            : ToolResult.Fail(
                outcome.ErrorCode ?? "write_failed",
                summary,
                outcome.Warnings,
                data);
    }

    private sealed class WriteAbortSignal : Exception
    {
    }
}
