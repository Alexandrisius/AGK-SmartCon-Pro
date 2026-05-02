namespace SmartCon.Core.Models;

public static class PipeConnectStateMachine
{
    private static readonly (PipeConnectState From, PipeConnectState To)[] Transitions =
    [
        (PipeConnectState.AwaitingStaticSelection, PipeConnectState.AwaitingDynamicSelection),
        (PipeConnectState.AwaitingStaticSelection, PipeConnectState.Cancelled),
        (PipeConnectState.AwaitingDynamicSelection, PipeConnectState.AligningConnectors),
        (PipeConnectState.AwaitingDynamicSelection, PipeConnectState.Cancelled),
        (PipeConnectState.AligningConnectors, PipeConnectState.ResolvingParameters),
        (PipeConnectState.AligningConnectors, PipeConnectState.Cancelled),
        (PipeConnectState.ResolvingParameters, PipeConnectState.ResolvingFittings),
        (PipeConnectState.ResolvingParameters, PipeConnectState.Cancelled),
        (PipeConnectState.ResolvingFittings, PipeConnectState.PostProcessing),
        (PipeConnectState.ResolvingFittings, PipeConnectState.Cancelled),
        (PipeConnectState.PostProcessing, PipeConnectState.Committed),
        (PipeConnectState.PostProcessing, PipeConnectState.Cancelled),
    ];

    private static readonly HashSet<PipeConnectState> TerminalStates =
    [
        PipeConnectState.Committed,
        PipeConnectState.Cancelled,
    ];

    public static bool CanTransition(PipeConnectState from, PipeConnectState to)
    {
        if (from == to) return true;
        foreach (var (f, t) in Transitions)
        {
            if (f == from && t == to) return true;
        }
        return false;
    }

    public static bool IsTerminal(PipeConnectState state) => TerminalStates.Contains(state);
}
