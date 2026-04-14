namespace SmartCon.Core.Models;

/// <summary>
/// Mutable context of a single PipeConnect session.
/// Lives at the ViewModel level, reset on cancel.
/// Do not store Element/Connector — only ElementId via ConnectorProxy (I-05).
/// </summary>
public sealed class PipeConnectionSession
{
    public ConnectorProxy? StaticConnector { get; set; }
    public ConnectorProxy? DynamicConnector { get; set; }
    public ConnectionGraph? DynamicChain { get; set; }
    public List<FittingMappingRule> ProposedFittings { get; set; } = [];
    public double RotationAngleDeg { get; set; }
    /// <summary>Phase 4: a reducer is needed (size matched approximately or S4 failed).</summary>
    public bool NeedsAdapter { get; set; }
    public double OriginalDynamicRadius { get; set; }
    public double ActualDynamicRadius { get; set; }
    public PipeConnectState State { get; set; } = PipeConnectState.AwaitingStaticSelection;

    /// <summary>
    /// Resets the session to its initial state.
    /// </summary>
    public void Reset()
    {
        StaticConnector = null;
        DynamicConnector = null;
        DynamicChain = null;
        ProposedFittings = [];
        RotationAngleDeg = 0;
        NeedsAdapter = false;
        OriginalDynamicRadius = 0;
        ActualDynamicRadius = 0;
        State = PipeConnectState.AwaitingStaticSelection;
    }
}
