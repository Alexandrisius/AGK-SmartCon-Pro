namespace SmartCon.Core.Models;

/// <summary>
/// Мутабельный контекст одной сессии соединения PipeConnect.
/// Живёт на уровне ViewModel, сбрасывается при отмене.
/// Не хранить Element/Connector — только ElementId через ConnectorProxy (I-05).
/// </summary>
public sealed class PipeConnectionSession
{
    public ConnectorProxy? StaticConnector { get; set; }
    public ConnectorProxy? DynamicConnector { get; set; }
    public ConnectionGraph? DynamicChain { get; set; }
    public List<FittingMappingRule> ProposedFittings { get; set; } = [];
    public double RotationAngleDeg { get; set; }
    /// <summary>Phase 4: нужен переходник (размер подобран приближённо или S4 провалился).</summary>
    public bool NeedsAdapter { get; set; }
    public double OriginalDynamicRadius { get; set; }
    public double ActualDynamicRadius { get; set; }
    public PipeConnectState State { get; set; } = PipeConnectState.AwaitingStaticSelection;

    /// <summary>
    /// Сбрасывает сессию в начальное состояние.
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
