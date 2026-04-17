using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

public interface IFittingChainResolver
{
    FittingChainPlan Resolve(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius);

    IReadOnlyList<FittingChainPlan> ResolveAlternatives(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius,
        int maxAlternatives = 3);

    // TODO [ChainV2]: Для multi-fitting цепочек добавить:
    // FittingChainPlan RecalculateWithRadius(FittingChainPlan current, int linkIndex, double newRadius);
    // FittingChainPlan ResolveWithMaxChainLength(int maxLength);
}
