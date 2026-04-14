namespace SmartCon.Core.Models;

/// <summary>
/// PipeConnect module state machine states.
/// Transitions are described in docs/pipeconnect/state-machine.md.
/// </summary>
public enum PipeConnectState
{
    AwaitingStaticSelection,
    AwaitingDynamicSelection,
    AligningConnectors,
    ResolvingParameters,
    ResolvingFittings,
    PostProcessing,
    Committed,
    Cancelled
}
