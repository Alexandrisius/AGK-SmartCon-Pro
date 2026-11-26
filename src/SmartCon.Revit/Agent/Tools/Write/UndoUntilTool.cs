using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Reverts every applied change set of the active document from the newest one
/// down to (and including) the requested set, in reverse order (G.10). Each set
/// is reverted with the exact undo_last mechanics — its own transaction, its own
/// rolled-back marking — so partial/conflict semantics stay identical. The chain
/// stops at the first set that cannot be reverted; the sets below it stay
/// applied and undo_last can finish them later.
/// </summary>
public sealed class UndoUntilTool : IAgentTool
{
    private readonly IRevitContext _revitContext;
    private readonly ITransactionService _transactions;
    private readonly IAgentChangeSetStore _changeSets;
    private readonly UndoLastTool _undoLast;

    public UndoUntilTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets,
        UndoLastTool undoLast)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _changeSets = changeSets ?? throw new ArgumentNullException(nameof(changeSets));
        _undoLast = undoLast ?? throw new ArgumentNullException(nameof(undoLast));
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "undo_until",
        Description =
            "Reverts ALL applied SmartCon change sets of the active document from the newest " +
            "down to the given changeSetId, inclusive, in reverse order — one undo_last per set. " +
            "Already rolled back/discarded sets are skipped automatically. The chain stops at " +
            "the first set that cannot be reverted (conflict/partial): it and the older sets " +
            "stay applied, and the report says where it stopped. dryRun defaults to true and " +
            "lists what would be restored plus conflicts. force=true restores conflicted items " +
            "anyway. Changes applied by start_job are not change sets — use rollback_job.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""changeSetId"":{""type"":""string"",""description"":""GUID of the oldest change set to revert (inclusive); take it from list_change_sets""},""dryRun"":{""type"":""boolean"",""default"":true},""force"":{""type"":""boolean"",""default"":false}},""required"":[""changeSetId""],""additionalProperties"":false}"
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await ExecuteCoreAsync(
                context.Revit,
                context.DocumentId,
                context.WriteApproved,
                args,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Test seam for integration tests: runs the whole flow on the calling
    /// (Revit API) thread with an explicit approval flag.
    /// </summary>
    internal Task<ToolResult> ExecuteForTests(
        Document doc,
        JsonElement args,
        bool approved,
        CancellationToken ct) =>
        ExecuteCoreAsync(
            new ImmediateAwaitableEvent(),
            AgentDocumentIdentity.GetDocumentId(doc),
            approved,
            args,
            ct);

    private async Task<ToolResult> ExecuteCoreAsync(
        IAgentAwaitableEvent revit,
        string expectedDocumentId,
        bool approved,
        JsonElement args,
        CancellationToken ct)
    {
        var dryRun = AgentJson.GetBool(args, "dryRun", true);
        if (!dryRun && !approved)
        {
            return ToolResult.Fail(
                "confirmation_required",
                "Reverting a chain of change sets requires explicit user confirmation. Call the " +
                "tool with dryRun=true first, show what would be restored, and only then repeat " +
                "the call with dryRun=false — the user will be asked to approve it.");
        }

        var raw = AgentJson.GetString(args, "changeSetId");
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out var targetId))
        {
            return ToolResult.Fail(
                "invalid_change_set_id",
                "changeSetId must be a GUID string; take it from list_change_sets.");
        }

        var documentId = await revit.RaiseAsync(
                _ =>
                {
                    var document = _revitContext.TryGetDocument();
                    return document is null
                        ? null
                        : AgentDocumentIdentity.GetDocumentId(document);
                },
                ct)
            .ConfigureAwait(false);

        if (documentId is null)
            return ToolResult.Fail("no_active_document", "No Revit project document is open.");

        if (!string.IsNullOrEmpty(expectedDocumentId)
            && !string.Equals(expectedDocumentId, documentId, StringComparison.Ordinal))
        {
            return ToolResult.Fail(
                "document_changed",
                "The active document changed while the request was running.");
        }

        var target = await _changeSets.GetAsync(targetId, ct).ConfigureAwait(false);
        if (target is null)
        {
            return ToolResult.Fail(
                "change_set_not_found",
                "Change set " + targetId.ToString("D") +
                " was not found. Call list_change_sets for the ids of this document.");
        }

        if (!string.Equals(target.Header.DocumentId, documentId, StringComparison.Ordinal))
        {
            return ToolResult.Fail(
                "document_mismatch",
                "Change set " + targetId.ToString("N") +
                " belongs to a different document.");
        }

        if (!target.Header.AppliedAt.HasValue)
        {
            return ToolResult.Fail(
                "change_set_not_applied",
                "Change set " + targetId.ToString("D") +
                " is not applied (already rolled back or never applied), so there is nothing " +
                "to revert down to.");
        }

        var headers = await _changeSets
            .ListAsync(documentId, AgentUndoChain.Window, 0, ct)
            .ConfigureAwait(false);

        var (chain, chainError, chainMessage) = AgentUndoChain.Build(targetId, headers);
        if (chainError is not null)
            return ToolResult.Fail(chainError, chainMessage!);

        var force = AgentJson.GetBool(args, "force", false);
        var report = await revit
            .RaiseAsync(_ => RunChain(documentId, chain, dryRun, force, ct), ct)
            .ConfigureAwait(false);

        if (report.FatalErrorCode is not null)
            return ToolResult.Fail(report.FatalErrorCode, report.FatalMessage ?? string.Empty);

        var payload = AgentJson.Serialize(new
        {
            dryRun,
            force,
            target = new
            {
                changeSetId = targetId.ToString("D"),
                operation = target.Header.Operation
            },
            totalChain = chain.Count,
            processed = report.Processed,
            notProcessed = report.NotProcessed,
            wouldRevert = dryRun ? report.RevertedCount : (int?)null,
            reverted = dryRun ? (int?)null : report.RevertedCount,
            affected = report.Affected
        });

        if (report.BlockedErrorCode is not null)
        {
            var done = report.RevertedCount + " set(s) " +
                       (dryRun ? "would be reverted" : "were reverted") +
                       " before the chain stopped";
            return ToolResult.Fail(
                report.BlockedErrorCode,
                done + ". Blocked at change set " + report.BlockedChangeSetId +
                " (" + report.BlockedOperation + "): " + report.BlockedReason +
                " The remaining " + report.NotProcessed.Count +
                " set(s) stay applied; undo_last/undo_until can finish them after the " +
                "conflict is resolved (force=true restores conflicted items anyway).",
                data: payload,
                affectedCount: report.Affected);
        }

        var text = dryRun
            ? "Dry run: " + report.RevertedCount + " change set(s) would be reverted down to '" +
              target.Header.Operation + "', " + report.Affected + " item(s) restored."
            : "Reverted " + report.RevertedCount + " change set(s) down to '" +
              target.Header.Operation + "' (" + targetId.ToString("D") + "), " +
              report.Affected + " item(s) restored.";

        return ToolResult.Ok(payload, text, report.Affected);
    }

    private ChainReport RunChain(
        string documentId,
        IReadOnlyList<ChangeSetHeader> chain,
        bool dryRun,
        bool force,
        CancellationToken ct)
    {
        var report = new ChainReport();

        var document = _revitContext.TryGetDocument();
        if (document is null)
        {
            report.FatalErrorCode = "no_active_document";
            report.FatalMessage = "No Revit project document is open.";
            return report;
        }

        if (!string.Equals(
                AgentDocumentIdentity.GetDocumentId(document),
                documentId,
                StringComparison.Ordinal))
        {
            report.FatalErrorCode = "document_changed";
            report.FatalMessage =
                "The active document changed while the request was running. " +
                "Start a new request in the current document.";
            return report;
        }

        var blocked = false;

        // A dry run must not borrow: probe the ownership of every chain target and
        // let undo_last skip blocked items, so the preview matches the apply.
        var worksharing = AgentWorksharingScope.Create(document);
        if (worksharing.NeedsCentral)
        {
            var targets = new HashSet<ElementId>();
            foreach (var header in chain)
            {
                var details = AsyncBridge.RunSync(
                    () => _changeSets.GetAsync(header.Id, CancellationToken.None));
                if (details is null)
                    continue;

                foreach (var item in details.Items)
                {
                    var element = UndoLastTool.ResolveElement(document, item);
                    if (element is not null)
                        targets.Add(element.Id);
                }
            }

            worksharing.Probe(targets, ct);
        }

        if (dryRun)
        {
            // One rolling-back transaction for the WHOLE chain: each older set
            // must see the state the newer reverts would produce. Per-set dry
            // runs would compare against the current model and report false
            // undo_conflict for every set below the newest.
            Exception? failure = null;
            _transactions.RunAndRollback("undo_until (dry run)", doc =>
            {
                try
                {
                    foreach (var header in chain)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (blocked)
                        {
                            report.NotProcessed.Add(new SetSummary
                            {
                                ChangeSetId = header.Id.ToString("D"),
                                Operation = header.Operation
                            });
                            continue;
                        }

                        var outcome = new WriteOutcome();
                        var args = AgentJson.Serialize(new
                        {
                            changeSetId = header.Id.ToString("D"),
                            force,
                            dryRun = true
                        });

                        _undoLast.ApplyForTests(doc, args, outcome, dryRun: true, ct, worksharing);
                        var entry = SummarizeOutcome(header, outcome);
                        report.Processed.Add(entry);
                        report.Affected += outcome.AffectedCount;

                        if (outcome.Success || outcome.ErrorCode == "nothing_to_undo")
                        {
                            if (outcome.Success)
                                report.RevertedCount++;
                            continue;
                        }

                        blocked = true;
                        report.BlockedErrorCode = outcome.ErrorCode ?? "undo_failed";
                        report.BlockedChangeSetId = header.Id.ToString("D");
                        report.BlockedOperation = header.Operation;
                        report.BlockedReason = outcome.Message ?? string.Empty;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            if (failure is not null)
            {
                report.FatalErrorCode = "tool_failed";
                report.FatalMessage = failure.GetType().Name + ": " + failure.Message;
            }

            return report;
        }

        foreach (var header in chain)
        {
            ct.ThrowIfCancellationRequested();

            if (blocked)
            {
                report.NotProcessed.Add(new SetSummary
                {
                    ChangeSetId = header.Id.ToString("D"),
                    Operation = header.Operation
                });
                continue;
            }

            var args = AgentJson.Serialize(new
            {
                changeSetId = header.Id.ToString("D"),
                force,
                dryRun = false
            });

            var result = _undoLast.ExecuteForTests(args, approved: true, ct);
            var entry = Summarize(header, result);
            report.Processed.Add(entry);
            report.Affected += result.AffectedCount;

            if (result.Success || result.ErrorCode == "nothing_to_undo")
            {
                if (result.Success)
                    report.RevertedCount++;
                continue;
            }

            blocked = true;
            report.BlockedErrorCode = result.ErrorCode ?? "undo_failed";
            report.BlockedChangeSetId = header.Id.ToString("D");
            report.BlockedOperation = header.Operation;
            report.BlockedReason = result.Message ?? string.Empty;
        }

        return report;
    }

    private static SetSummary SummarizeOutcome(ChangeSetHeader header, WriteOutcome outcome)
    {
        var entry = new SetSummary
        {
            ChangeSetId = header.Id.ToString("D"),
            Operation = header.Operation,
            Affected = outcome.AffectedCount,
            Skipped = outcome.Batch.Skipped.Count,
            Failed = outcome.Batch.Failed.Count
        };

        FillStatus(entry, outcome.Success, outcome.ErrorCode, outcome.Message);
        return entry;
    }

    private static SetSummary Summarize(ChangeSetHeader header, ToolResult result)
    {
        var entry = new SetSummary
        {
            ChangeSetId = header.Id.ToString("D"),
            Operation = header.Operation,
            Affected = result.AffectedCount
        };

        if (result.Data is { } data && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("failed", out var failed) && failed.ValueKind == JsonValueKind.Array)
                entry.Failed = failed.GetArrayLength();
            if (data.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.Array)
                entry.Skipped = skipped.GetArrayLength();
        }

        FillStatus(entry, result.Success, result.ErrorCode, result.Message);
        return entry;
    }

    private static void FillStatus(
        SetSummary entry,
        bool success,
        string? errorCode,
        string? message)
    {
        if (errorCode == "nothing_to_undo")
        {
            entry.Status = "alreadyRestored";
            return;
        }

        if (!success)
        {
            entry.Status = "blocked";
            entry.ErrorCode = errorCode;
            entry.Reason = message;
        }
        else
        {
            entry.Status = "reverted";
        }
    }

    private sealed class ChainReport
    {
        public List<SetSummary> Processed { get; } = new();

        public List<SetSummary> NotProcessed { get; } = new();

        public int RevertedCount { get; set; }

        public int Affected { get; set; }

        public string? FatalErrorCode { get; set; }

        public string? FatalMessage { get; set; }

        public string? BlockedErrorCode { get; set; }

        public string? BlockedChangeSetId { get; set; }

        public string? BlockedOperation { get; set; }

        public string BlockedReason { get; set; } = string.Empty;
    }

    private sealed class SetSummary
    {
        public string ChangeSetId { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public int Affected { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; set; }

        public string? ErrorCode { get; set; }

        public string? Reason { get; set; }
    }
}
