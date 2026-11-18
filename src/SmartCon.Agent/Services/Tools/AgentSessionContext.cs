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

    /// <summary>
    /// Tools enabled through load_tool_pack in this conversation. The set is
    /// merged into the catalog sent to the model from the next round on.
    /// </summary>
    public HashSet<string> LoadedToolNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Tools queued by load_tool_pack during the current round. The orchestrator
    /// moves them into <see cref="LoadedToolNames"/> at the next round boundary.
    /// </summary>
    public HashSet<string> PendingToolNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-session prompt-token calibration, fed by provider usage after every
    /// completion and applied to local estimates before compaction.
    /// </summary>
    public AgentTokenCalibration TokenCalibration { get; } = new();

    /// <summary>
    /// Structured working state of the current conversation. Loaded and saved
    /// by the orchestrator only when <c>SMARTCON_AGENT_WORKING_STATE</c> is on.
    /// </summary>
    public WorkingState? WorkingState { get; set; }

    /// <summary>Clears the tools loaded for a different conversation.</summary>
    public void ResetToolSet()
    {
        LoadedToolNames.Clear();
        PendingToolNames.Clear();
    }
}
