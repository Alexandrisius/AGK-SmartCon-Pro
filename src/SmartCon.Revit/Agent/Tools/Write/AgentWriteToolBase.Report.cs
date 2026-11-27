using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Report half of the write pipeline: the change-set audit trail and the payload
/// (changed/skipped/failed, dry-run text, partial-batch semantics).
/// </summary>
public abstract partial class AgentWriteToolBase
{
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
            if (outcome.Batch.Changed.Count == 0)
            {
                // Nothing passed: "the passing items are ready to apply" used to
                // send the model into an empty apply (dogfood v16, Ops-1).
                summary += " " + outcome.Batch.Failed.Count +
                           " item(s) failed validation and NONE passed — there is nothing " +
                           "to apply. [Action: fix them from the error codes/availableFields " +
                           "in the result, then repeat the dry run]";
            }
            else
            {
                summary += " " + outcome.Batch.Failed.Count +
                           " item(s) failed validation and will fail again: [" + failedIds +
                           "] (max 10). Repeat the SAME call with dryRun:false WITHOUT the " +
                           "failed items — the passing items are ready to apply.";
            }
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
}
