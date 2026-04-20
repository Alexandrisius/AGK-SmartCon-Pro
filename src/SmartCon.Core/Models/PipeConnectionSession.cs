namespace SmartCon.Core.Models;

/// <summary>
/// Immutable session state for the PipeConnect flow (S1–S8).
/// Accumulated progressively: first static, then dynamic, then alignment, fittings, etc.
/// Each With* method returns a new instance (record with-expression).
/// </summary>
public sealed record PipeConnectionSession
{
    /// <summary>Selected static connector (second pick).</summary>
    public ConnectorProxy? StaticConnector { get; init; }

    /// <summary>Selected dynamic connector (first pick).</summary>
    public ConnectorProxy? DynamicConnector { get; init; }

    /// <summary>Chain graph of the dynamic element. Null if no connected chain.</summary>
    public ConnectionGraph? DynamicChain { get; init; }

    /// <summary>Fitting mapping rules proposed for the connection.</summary>
    public IReadOnlyList<FittingMappingRule> ProposedFittings { get; init; } = [];

    /// <summary>User-specified rotation angle in degrees.</summary>
    public double RotationAngleDeg { get; init; }

    /// <summary>Whether a reducer (adapter) is needed due to size mismatch.</summary>
    public bool NeedsAdapter { get; init; }

    /// <summary>Dynamic connector radius before any adjustments.</summary>
    public double OriginalDynamicRadius { get; init; }

    /// <summary>Dynamic connector radius after adjustments.</summary>
    public double ActualDynamicRadius { get; init; }

    /// <summary>Current state machine state.</summary>
    public PipeConnectState State { get; init; } = PipeConnectState.AwaitingStaticSelection;

    /// <summary>Empty session with default state.</summary>
    public static PipeConnectionSession Empty { get; } = new();

    /// <summary>Return a copy with the specified static connector.</summary>
    public PipeConnectionSession WithStaticConnector(ConnectorProxy? connector) =>
        this with { StaticConnector = connector };

    /// <summary>Return a copy with the specified dynamic connector.</summary>
    public PipeConnectionSession WithDynamicConnector(ConnectorProxy? connector) =>
        this with { DynamicConnector = connector };

    /// <summary>Return a copy with the specified dynamic chain graph.</summary>
    public PipeConnectionSession WithDynamicChain(ConnectionGraph? chain) =>
        this with { DynamicChain = chain };

    /// <summary>Return a copy with the proposed fitting rules.</summary>
    public PipeConnectionSession WithProposedFittings(IReadOnlyList<FittingMappingRule> fittings) =>
        this with { ProposedFittings = fittings };

    /// <summary>Return a copy with the specified rotation angle.</summary>
    public PipeConnectionSession WithRotationAngleDeg(double angle) =>
        this with { RotationAngleDeg = angle };

    /// <summary>Return a copy with the specified adapter flag.</summary>
    public PipeConnectionSession WithNeedsAdapter(bool needs) =>
        this with { NeedsAdapter = needs };

    /// <summary>Return a copy with the original dynamic radius.</summary>
    public PipeConnectionSession WithOriginalDynamicRadius(double radius) =>
        this with { OriginalDynamicRadius = radius };

    /// <summary>Return a copy with the actual dynamic radius.</summary>
    public PipeConnectionSession WithActualDynamicRadius(double radius) =>
        this with { ActualDynamicRadius = radius };

    /// <summary>Return a copy with the specified state machine state.</summary>
    public PipeConnectionSession WithState(PipeConnectState state) =>
        this with { State = state };

    /// <summary>Reset the session to the initial empty state.</summary>
    public PipeConnectionSession Reset() => Empty;
}
