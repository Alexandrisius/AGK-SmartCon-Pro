using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed class AgentSessionContext
{
    public string DocumentId { get; set; } = string.Empty;

    public Guid ConversationId { get; set; }

    /// <summary>
    /// Confirmation policy of the current session. Reset to ConfirmWrites when
    /// the active document changes (see AgentOrchestrator).
    /// </summary>
    public AgentApprovalMode ApprovalMode { get; set; } = AgentApprovalMode.ConfirmWrites;

    /// <summary>
    /// Tools sent to the model in the current turn (routed shortlist or the full
    /// catalog after find_tools). find_tools marks matching results as available.
    /// </summary>
    public IReadOnlyCollection<string> ActiveToolNames { get; set; } = Array.Empty<string>();
}
