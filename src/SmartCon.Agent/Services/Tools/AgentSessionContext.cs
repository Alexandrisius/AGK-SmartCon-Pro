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
    /// Tools enabled through load_tool_pack in this conversation, ordered from the
    /// least to the most recently requested or used tool. A request carries only
    /// a bounded slice of the catalog, so the tail of this list decides which
    /// loaded tools the model can actually call.
    /// </summary>
    public List<string> LoadedToolNames { get; } = new();

    /// <summary>
    /// Tools queued by load_tool_pack during the current round. The orchestrator
    /// moves them into <see cref="LoadedToolNames"/> at the next round boundary.
    /// </summary>
    public HashSet<string> PendingToolNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks a tool as loaded and moves it to the most-recent position, so an
    /// explicit load or an actual call survives the request cap even when the
    /// conversation has already loaded more tools than one request can carry.
    /// </summary>
    public void TouchTool(string name)
    {
        if (string.IsNullOrEmpty(name))
            return;

        LoadedToolNames.RemoveAll(
            candidate => string.Equals(candidate, name, StringComparison.Ordinal));
        LoadedToolNames.Add(name);
    }

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
        LastDocumentVersion = null;
    }

    /// <summary>
    /// documentVersion marker of the last tool call. A *_not_found failure whose
    /// marker differs from the previous call means the model changed between the
    /// calls (rollback, reopen, undo) rather than the id being invalid.
    /// </summary>
    public string? LastDocumentVersion { get; set; }
}
