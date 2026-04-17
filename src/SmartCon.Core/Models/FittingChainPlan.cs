using System.Linq;

namespace SmartCon.Core.Models;

public sealed class FittingChainPlan
{
    public required ConnectionTypeCode StaticCtc { get; init; }
    public required ConnectionTypeCode DynamicCtc { get; init; }
    public required double StaticRadius { get; init; }
    public required double DynamicRadius { get; init; }
    public required ChainTopology Topology { get; init; }
    public IReadOnlyList<FittingChainLink> Links { get; init; } = [];

    public bool IsDirect => Topology == ChainTopology.Direct;
    public bool HasReducer => Links.Any(l => l.Type == FittingChainNodeType.Reducer);
    public int FittingCount => Links.Count(l => l.Type == FittingChainNodeType.Fitting);
    public int ReducerCount => Links.Count(l => l.Type == FittingChainNodeType.Reducer);

    // TODO [ChainV2]: Для поддержки multi-fitting цепочек (3+ звена) добавить:
    // - ValidateChain() — проверка совместимости каждого звена с соседями
    // - IntermediateCtc(int index) — CTC между звеньями
    // - IntermediateRadius(int index) — DN между звеньями
}
