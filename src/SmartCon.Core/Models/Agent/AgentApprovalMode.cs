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
    /// Writes are applied without a per-action dialog. Hard guards (destructive
    /// risk, workshared document, more than 200 affected items, document change)
    /// still require confirmation. Every write creates a change set.
    /// </summary>
    AutoApproveSession = 1,

    /// <summary>Read-only: every non-read tool is rejected with read_only_mode.</summary>
    ReadOnly = 2
}
