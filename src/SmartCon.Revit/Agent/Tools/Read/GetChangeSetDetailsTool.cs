using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Full record of one SmartCon change set of the active document (G.10): header
/// plus every recorded item (element ids, parameter, old/new value, status) —
/// the input undo_until needs to pick the point to revert down to.
/// </summary>
public sealed class GetChangeSetDetailsTool : AgentReadToolBase
{
    private readonly IAgentChangeSetStore _changeSets;

    public GetChangeSetDetailsTool(IRevitContext revitContext, IAgentChangeSetStore changeSets)
        : base(revitContext)
    {
        _changeSets = changeSets ?? throw new ArgumentNullException(nameof(changeSets));
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_change_set_details",
        Description =
            "Returns one SmartCon change set by changeSetId (GUID from list_change_sets): header " +
            "(operation, timestamps, status) and the recorded items — uniqueId, elementId, " +
            "parameterName, oldValue, newValue, status. Use the item list to explain what an " +
            "operation changed and to pick the target of undo_until.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""changeSetId"":{""type"":""string"",""description"":""GUID of the change set (list_change_sets)""}},""required"":[""changeSetId""],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var raw = AgentJson.GetString(args, "changeSetId");
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out var changeSetId))
        {
            return ToolResult.Fail(
                "invalid_change_set_id",
                "changeSetId must be a GUID string; take it from list_change_sets.");
        }

        var details = AsyncBridge.RunSync(() => _changeSets.GetAsync(changeSetId, ct));
        if (details is null)
        {
            return ToolResult.Fail(
                "change_set_not_found",
                "Change set " + changeSetId.ToString("D") +
                " was not found. Call list_change_sets for the ids of this document.");
        }

        var documentId = AgentDocumentIdentity.GetDocumentId(doc);
        if (!string.Equals(details.Header.DocumentId, documentId, StringComparison.Ordinal))
        {
            return ToolResult.Fail(
                "change_set_foreign_document",
                "Change set " + changeSetId.ToString("D") +
                " belongs to another document.");
        }

        var header = details.Header;
        var status = AgentChangeSetStatus.Derive(header, details.Items);
        var payload = AgentJson.Serialize(new
        {
            changeSetId = header.Id.ToString("D"),
            operation = header.Operation,
            description = header.Description,
            documentPath = header.DocumentPath,
            revitVersion = header.RevitVersion,
            author = header.Author,
            createdAt = header.CreatedAt,
            appliedAt = header.AppliedAt,
            status,
            itemCount = details.Items.Count,
            items = details.Items.Select(item => new
            {
                uniqueId = item.UniqueId,
                elementId = item.ElementId,
                parameterName = item.ParameterName,
                oldValue = item.OldValue,
                newValue = item.NewValue,
                status = item.Status.ToString()
            }).ToList()
        });

        return ToolResult.Ok(
            payload,
            "Change set '" + header.Operation + "' (" + status + "), " +
            details.Items.Count + " item(s).");
    }
}
