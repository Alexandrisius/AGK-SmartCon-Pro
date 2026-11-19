namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Confirmation policy of an Agent session. The mode is a property of the
/// session and resets when the active document changes.
/// </summary>
public enum AgentApprovalMode
{
    /// <summary>Every write with dryRun=false asks the user (default).</summary>
    ConfirmWrites = 0,

    /// <summary>
    /// Bypass: writes are applied with no dialog and no risk gate at all — the
    /// operator explicitly took the risk (for example, left the desk and wants
    /// the agent to finish the job). Every write still creates a change set, so
    /// undo_last can revert it.
    /// </summary>
    AutoApproveSession = 1,

    /// <summary>Read-only: every non-read tool is rejected with read_only_mode.</summary>
    ReadOnly = 2
}
