namespace SmartCon.Core.Models;

/// <summary>
/// Describes the composition of a fitting chain between two connectors.
/// Ordered by increasing complexity.
/// </summary>
public enum ChainTopology
{
    /// <summary>No fitting needed — connectors match directly in type and size.</summary>
    Direct,

    /// <summary>Only a reducer (DN transition, same connection type).</summary>
    ReducerOnly,

    /// <summary>Only a fitting (type transition, same DN).</summary>
    FittingOnly,

    /// <summary>Fitting followed by a reducer.</summary>
    ReducerFitting,

    /// <summary>Fitting preceded by a reducer.</summary>
    FittingReducer,

    /// <summary>Chain of multiple fittings.</summary>
    FittingChain,

    /// <summary>Chain of fittings followed by a reducer.</summary>
    FittingChainReducer,

    /// <summary>Reducer followed by a chain of fittings.</summary>
    ReducerFittingChain,

    /// <summary>Complex multi-link chain not covered by simpler topologies.</summary>
    ComplexChain
}
