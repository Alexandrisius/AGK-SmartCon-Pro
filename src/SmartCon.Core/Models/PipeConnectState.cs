namespace SmartCon.Core.Models;

/// <summary>
/// PipeConnect module state machine states.
/// Transitions are described in docs/pipeconnect/state-machine.md.
/// </summary>
public enum PipeConnectState
{
    /// <summary>Waiting for the user to select the first (dynamic) element.</summary>
    AwaitingStaticSelection,

    /// <summary>Waiting for the user to select the second (static) element.</summary>
    AwaitingDynamicSelection,

    /// <summary>Computing connector alignment (position + rotation).</summary>
    AligningConnectors,

    /// <summary>Resolving size parameters (DN adjustment, lookup table).</summary>
    ResolvingParameters,

    /// <summary>Resolving fitting mapping rules and chain plan.</summary>
    ResolvingFittings,

    /// <summary>Post-processing: validation, position correction, final adjustments.</summary>
    PostProcessing,

    /// <summary>Transaction group assimilated — changes committed.</summary>
    Committed,

    /// <summary>User cancelled — full rollback.</summary>
    Cancelled
}
