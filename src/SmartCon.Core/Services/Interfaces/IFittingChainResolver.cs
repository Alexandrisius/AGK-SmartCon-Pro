using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Resolves a fitting chain plan for a pair of connector types and radii.
/// Determines the topology (direct, reducer-only, fitting+reducer, etc.)
/// and selects concrete fitting families for each chain link.
/// </summary>
public interface IFittingChainResolver
{
    /// <summary>
    /// Resolve the best fitting chain plan for the given connector pair and radii.
    /// </summary>
    /// <param name="staticCtc">Connection type of the static connector.</param>
    /// <param name="dynamicCtc">Connection type of the dynamic connector.</param>
    /// <param name="staticRadius">Static connector radius (internal units).</param>
    /// <param name="dynamicRadius">Dynamic connector radius (internal units).</param>
    /// <returns>A chain plan describing topology and fitting links.</returns>
    FittingChainPlan Resolve(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius);

    /// <summary>
    /// Resolve alternative chain plans (up to <paramref name="maxAlternatives"/>),
    /// ordered by priority.
    /// </summary>
    IReadOnlyList<FittingChainPlan> ResolveAlternatives(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius,
        int maxAlternatives = 3);

    // TODO [ChainV2]: Для multi-fitting цепочек добавить:
    // FittingChainPlan RecalculateWithRadius(FittingChainPlan current, int linkIndex, double newRadius);
    // FittingChainPlan ResolveWithMaxChainLength(int maxLength);
}
