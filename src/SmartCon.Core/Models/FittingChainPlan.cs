using System.Linq;

namespace SmartCon.Core.Models;

/// <summary>
/// Describes the complete plan for connecting two elements through a chain of fittings and/or reducers.
/// Produced by <see cref="IFittingChainResolver"/>.
/// </summary>
public sealed class FittingChainPlan
{
    /// <summary>Connection type code of the static connector.</summary>
    public required ConnectionTypeCode StaticCtc { get; init; }

    /// <summary>Connection type code of the dynamic connector.</summary>
    public required ConnectionTypeCode DynamicCtc { get; init; }

    /// <summary>Static connector radius (internal units).</summary>
    public required double StaticRadius { get; init; }

    /// <summary>Dynamic connector radius (internal units).</summary>
    public required double DynamicRadius { get; init; }

    /// <summary>Topology of the chain (direct, reducer-only, fitting+reducer, etc.).</summary>
    public required ChainTopology Topology { get; init; }

    /// <summary>Ordered list of chain links (fittings and reducers).</summary>
    public IReadOnlyList<FittingChainLink> Links { get; init; } = [];

    /// <summary>True when no intermediate fittings are needed.</summary>
    public bool IsDirect => Topology == ChainTopology.Direct;

    /// <summary>Whether the chain contains at least one reducer.</summary>
    public bool HasReducer => Links.Any(l => l.Type == FittingChainNodeType.Reducer);

    /// <summary>Number of fitting links in the chain.</summary>
    public int FittingCount => Links.Count(l => l.Type == FittingChainNodeType.Fitting);

    /// <summary>Number of reducer links in the chain.</summary>
    public int ReducerCount => Links.Count(l => l.Type == FittingChainNodeType.Reducer);

    // TODO [ChainV2]: Для поддержки multi-fitting цепочек (3+ звена) добавить:
    // - ValidateChain() — проверка совместимости каждого звена с соседями
    // - IntermediateCtc(int index) — CTC между звеньями
    // - IntermediateRadius(int index) — DN между звеньями
}
