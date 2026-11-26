using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Batch targets of an undo: the change set is stored by UniqueId, so the
/// ownership of its elements is taken before the transaction.
/// </summary>
public sealed partial class UndoLastTool
{
    protected override IEnumerable<ElementId> CollectWriteTargets(
        Document document,
        JsonElement args)
    {
        // The elements of a change set are addressed by UniqueId, so the batch
        // ownership must be taken from the stored items before the transaction.
        var details = TryLoadDetails(document, args);
        if (details is null)
            yield break;

        foreach (var item in details.Items)
        {
            var element = ResolveElement(document, item);
            if (element is not null)
                yield return element.Id;
        }
    }

    private ChangeSetDetails? TryLoadDetails(Document document, JsonElement args)
    {
        try
        {
            var requestedId = AgentJson.GetString(args, "changeSetId");
            ChangeSetDetails? details;
            if (!string.IsNullOrWhiteSpace(requestedId))
            {
                if (!Guid.TryParse(requestedId, out var parsedId))
                    return null;

                details = AsyncBridge.RunSync(() => ChangeSets.GetAsync(parsedId, CancellationToken.None));
            }
            else
            {
                details = AsyncBridge.RunSync(() => ChangeSets.GetLastAppliedAsync(
                    AgentDocumentIdentity.GetDocumentId(document), CancellationToken.None));
            }

            return details is not null
                   && string.Equals(
                       details.Header.DocumentId,
                       AgentDocumentIdentity.GetDocumentId(document),
                       StringComparison.Ordinal)
                ? details
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
