namespace SmartCon.Core.Models;

public enum ChainTopology
{
    Direct,
    ReducerOnly,
    FittingOnly,
    ReducerFitting,
    FittingReducer,
    FittingChain,
    FittingChainReducer,
    ReducerFittingChain,
    ComplexChain
}
