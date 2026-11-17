using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Core.Models.Agent;

public sealed class AgentToolContext
{
    public AgentToolContext(
        IAgentAwaitableEvent revit,
        string documentId,
        Guid conversationId)
    {
        Revit = revit ?? throw new ArgumentNullException(nameof(revit));
        DocumentId = documentId ?? throw new ArgumentNullException(nameof(documentId));
        ConversationId = conversationId;
    }

    public IAgentAwaitableEvent Revit { get; }

    public string DocumentId { get; }

    public Guid ConversationId { get; }

    /// <summary>
    /// True when the user explicitly approved this exact write action
    /// (approval digest verified by the tool executor). Write tools must refuse
    /// to modify the model when this flag is false and dryRun is false.
    /// </summary>
    public bool WriteApproved { get; init; }
}