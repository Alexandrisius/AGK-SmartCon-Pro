using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the SmartCon change sets of the active document in reverse-chronological
/// order (G.10): id, operation, timestamps, item count and applied/rolledBack/
/// discarded status. The header list alone has no item counts, so one store
/// lookup per page row is used to report them.
/// </summary>
public sealed class ListChangeSetsTool : AgentReadToolBase
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    private readonly IAgentChangeSetStore _changeSets;

    public ListChangeSetsTool(IRevitContext revitContext, IAgentChangeSetStore changeSets)
        : base(revitContext)
    {
        _changeSets = changeSets ?? throw new ArgumentNullException(nameof(changeSets));
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_change_sets",
        Description =
            "Lists the SmartCon change sets of the active document, newest first: changeSetId, " +
            "operation, createdAt, appliedAt, itemCount and status (applied/rolledBack/discarded). " +
            "Applied sets can be reverted with undo_last/undo_until; rolled back and discarded " +
            "ones are history only. Paged: limit (default 20, max 100), offset, returned, " +
            "nextOffset. fields projects the rows like other list tools.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""limit"":{""type"":""integer"",""default"":20,""maximum"":100},""offset"":{""type"":""integer"",""default"":0},""fields"":{""type"":""array"",""description"":""Allowed: changeSetId,operation,description,createdAt,appliedAt,itemCount,status,author. Returned keys match the requested names."",""items"":{""type"":""string""}}},""additionalProperties"":false}"
    };

    public override bool SupportsDocumentRouting => false;

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var limit = Math.Max(1, Math.Min(AgentJson.GetInt(args, "limit", DefaultLimit), MaxLimit));
        var offset = Math.Max(0, AgentJson.GetInt(args, "offset", 0));
        var fields = AgentJson.GetStringArray(args, "fields");
        var documentId = AgentDocumentIdentity.GetDocumentId(doc);

        var headers = AsyncBridge.RunSync(() => _changeSets.ListAsync(documentId, limit, offset, ct));

        var rows = new List<object>(headers.Count);
        foreach (var header in headers)
        {
            ct.ThrowIfCancellationRequested();
            var details = AsyncBridge.RunSync(() => _changeSets.GetAsync(header.Id, ct));
            var items = details?.Items ?? Array.Empty<ChangeItem>();
            rows.Add(new
            {
                changeSetId = header.Id.ToString("D"),
                operation = header.Operation,
                description = header.Description,
                createdAt = header.CreatedAt,
                appliedAt = header.AppliedAt,
                itemCount = items.Count,
                status = AgentChangeSetStatus.Derive(header, items),
                author = header.Author
            });
        }

        var hasMore = rows.Count == limit;
        var projected = rows;
        IReadOnlyList<string>? ignored = null;
        if (fields.Count > 0)
        {
            var ignoredFields = new List<string>();
            var substituted = new Dictionary<string, string>(StringComparer.Ordinal);
            var cards = new List<JsonElement>(rows.Count);
            foreach (var row in rows)
            {
                cards.Add(AgentCardProjection.Apply(
                    AgentJson.SerializeCard(row), fields, ignoredFields, substituted));
            }

            projected = cards.Cast<object>().ToList();
            if (ignoredFields.Count > 0)
                ignored = ignoredFields;
        }

        var payload = AgentJson.Serialize(new
        {
            documentId,
            offset,
            returned = projected.Count,
            nextOffset = hasMore ? offset + projected.Count : (int?)null,
            changeSets = projected,
            ignoredFields = ignored
        });

        return ToolResult.Ok(
            payload,
            projected.Count + " change set(s) of this document, newest first" +
            (hasMore ? ". The list is truncated — pass nextOffset as offset." : "."));
    }
}
