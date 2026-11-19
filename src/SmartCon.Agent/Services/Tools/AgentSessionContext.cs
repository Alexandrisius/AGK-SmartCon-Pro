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
    /// Tools sent to the model in the current turn (frozen for the conversation
    /// and re-sorted by name before every request). find_tools marks matching
    /// results as available.
    /// </summary>
    public IReadOnlyCollection<string> ActiveToolNames { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Tool set of the conversation (P.4). Chosen once from the first user text
    /// plus the always-available tools and every loaded tool, then only extended
    /// by <c>load_tool_pack</c> at a round boundary. The payload is built from
    /// this set in name order, so the tool block stays byte-identical between
    /// turns and the provider cache survives.
    /// </summary>
    public List<string> FrozenToolNames { get; } = new();

    /// <summary>
    /// True once the conversation tool set has been chosen. An intentionally
    /// empty set (no always-available tool and no shortlist match in a synthetic
    /// registry) must not be re-selected on the next turn.
    /// </summary>
    public bool ToolSetFrozen { get; set; }

    public bool FreezeTool(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        if (FrozenToolNames.Contains(name, StringComparer.Ordinal))
            return false;

        FrozenToolNames.Add(name);
        return true;
    }

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
    /// Conversation and document the persisted &lt;session_context&gt; baseline
    /// belongs to. Empty until the first turn checks the history.
    /// </summary>
    public Guid SessionContextConversationId { get; set; }

    public string? SessionContextDocumentId { get; set; }

    public bool SessionContextWritten { get; set; }

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
        FrozenToolNames.Clear();
        ToolSetFrozen = false;
        LastDocumentVersion = null;
    }

    /// <summary>
    /// documentVersion marker of the last tool call. A *_not_found failure whose
    /// marker differs from the previous call means the model changed between the
    /// calls (rollback, reopen, undo) rather than the id being invalid.
    /// </summary>
    public string? LastDocumentVersion { get; set; }
}
