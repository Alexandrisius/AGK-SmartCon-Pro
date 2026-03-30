namespace SmartCon.Core.Models;

/// <summary>
/// Состояния state machine модуля PipeConnect.
/// Переходы описаны в docs/pipeconnect/state-machine.md.
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
