using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// C2: closes a SmartCon change set without touching the model. This breaks the
/// "permanently blocked undo" loop — after a discard, the next unnamed
/// undo_last targets the previous applied set. The model is never modified.
/// </summary>
public sealed class DiscardChangeSetTool : IAgentTool
{
    private readonly IRevitContext _revitContext;
    private readonly IAgentChangeSetStore _changeSets;

    public DiscardChangeSetTool(
        IRevitContext revitContext,
        IAgentChangeSetStore changeSets)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
        _changeSets = changeSets ?? throw new ArgumentNullException(nameof(changeSets));
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "discard_change_set",
        Description =
            "Closes a SmartCon change set without changing the model, so the next undo_last " +
            "targets the previous set. Use it when undo_last reports undo_conflict for a set " +
            "that can no longer be reverted (its old name/id is now used by another element). " +
            "Pass changeSetId, or omit it to discard the last applied set. The model is never " +
            "modified.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = false,
        Category = "Write",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""changeSetId"":{""type"":""string""}},""additionalProperties"":false}"
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!context.WriteApproved)
        {
            return ToolResult.Fail(
                "confirmation_required",
                "Closing a change set requires explicit user confirmation.");
        }

        var documentId = await context.Revit.RaiseAsync(
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

        if (!string.IsNullOrEmpty(context.DocumentId)
            && !string.Equals(context.DocumentId, documentId, StringComparison.Ordinal))
        {
            return ToolResult.Fail(
                "document_changed",
                "The active document changed while the request was running.");
        }

        var request = ParseRequest(args);
        if (request.ErrorCode is not null)
            return ToolResult.Fail(request.ErrorCode, request.Message!);

        Guid? changeSetId = request.ChangeSetId;
        if (!changeSetId.HasValue)
        {
            var last = await _changeSets
                .GetLastAppliedAsync(documentId, ct)
                .ConfigureAwait(false);
            if (last is not null)
                changeSetId = last.Header.Id;
        }

        if (!changeSetId.HasValue)
        {
            return ToolResult.Fail(
                "no_change_set",
                "There is no applied SmartCon change set in this document to discard.");
        }

        var details = await _changeSets.GetAsync(changeSetId.Value, ct).ConfigureAwait(false);
        if (details is null)
            return ToolResult.Fail(
                "change_set_not_found",
                "Change set " + changeSetId.Value.ToString("D") + " was not found.");

        if (!string.Equals(details.Header.DocumentId, documentId, StringComparison.Ordinal))
        {
            return ToolResult.Fail(
                "change_set_foreign_document",
                "Change set " + changeSetId.Value.ToString("D") +
                " belongs to another document.");
        }

        if (details.Header.AppliedAt is null)
        {
            return ToolResult.Fail(
                "change_set_not_applied",
                "Change set " + changeSetId.Value.ToString("D") +
                " is not an applied change set; there is nothing to discard.");
        }

        await _changeSets
            .MarkDiscardedAsync(changeSetId.Value, DateTime.UtcNow, ct)
            .ConfigureAwait(false);

        var payload = AgentJson.Serialize(new
        {
            discarded = true,
            changeSetId = changeSetId.Value.ToString("D"),
            operation = details.Header.Operation,
            items = details.Items.Count
        });

        return ToolResult.Ok(
            payload,
            "Change set '" + details.Header.Operation + "' was closed without changing the " +
            "model; the next undo_last targets the previous set.",
            details.Items.Count);
    }

    private static DiscardRequest ParseRequest(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("changeSetId", out var raw)
            || raw.ValueKind == JsonValueKind.Null)
        {
            return new DiscardRequest(null, null, null);
        }

        if (raw.ValueKind != JsonValueKind.String
            || !Guid.TryParse(raw.GetString(), out var parsed))
        {
            return new DiscardRequest(
                null,
                "invalid_change_set_id",
                "changeSetId must be a GUID string; the last change set was left untouched.");
        }

        return new DiscardRequest(parsed, null, null);
    }

    private readonly record struct DiscardRequest(
        Guid? ChangeSetId,
        string? ErrorCode,
        string? Message);
}
