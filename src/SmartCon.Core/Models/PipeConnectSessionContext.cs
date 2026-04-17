using SmartCon.Core.Math;

namespace SmartCon.Core.Models;

/// <summary>
/// Session context passed from PipeConnectCommand to PipeConnectEditorViewModel.
/// Contains S1-S5 analysis results performed BEFORE opening TransactionGroup.
/// Immutable — created once in PipeConnectCommand.Execute().
/// </summary>
public sealed class PipeConnectSessionContext
{
    public required ConnectorProxy StaticConnector { get; init; }
    public required ConnectorProxy DynamicConnector { get; init; }

    /// <summary>Alignment calculation result (S3, pure math).</summary>
    public required AlignmentResult AlignResult { get; init; }

    /// <summary>Target radius for the dynamic element (S4). null = sizes match.</summary>
    public double? ParamTargetRadius { get; init; }

    /// <summary>S4: a reducer is expected (size matched approximately).</summary>
    public bool ParamExpectNeedsAdapter { get; init; }

    /// <summary>
    /// Matched fittings from mapping (S5, ordered by Priority).
    /// Empty list = direct connection.
    /// </summary>
    public IReadOnlyList<FittingMappingRule> ProposedFittings { get; init; } = [];

    /// <summary>
    /// Resolved fitting chain plan (S5). null if resolver not invoked (legacy path).
    /// Contains ordered links (reducer + fitting, fitting + reducer, etc.).
    /// </summary>
    public FittingChainPlan? ChainPlan { get; init; }

    /// <summary>
    /// Chain graph of the dynamic element, built BEFORE disconnect. null = no chain.
    /// </summary>
    public ConnectionGraph? ChainGraph { get; init; }

    /// <summary>
    /// Multi-column lookup table constraints from other fitting connectors.
    /// Used for dropdown filtering and ConnectorRadiusExistsInTable checks.
    /// Empty list = no constraints (single-column or no table).
    /// </summary>
    public IReadOnlyList<LookupColumnConstraint> LookupConstraints { get; init; } = [];

    /// <summary>
    /// Virtual CTC assignment store. LoadFamily is deferred until Connect().
    /// Created in PipeConnectCommand, passed to ViewModel.
    /// </summary>
    public VirtualCtcStore VirtualCtcStore { get; init; } = new();
}
