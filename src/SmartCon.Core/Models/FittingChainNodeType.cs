namespace SmartCon.Core.Models;

/// <summary>
/// Type of a single node (link) in a fitting chain plan.
/// </summary>
public enum FittingChainNodeType
{
    /// <summary>Transition fitting (e.g. adapter, coupling, nipple).</summary>
    Fitting,

    /// <summary>Reducer for DN transition.</summary>
    Reducer
}
