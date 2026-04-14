using SmartCon.Core.Models;

namespace SmartCon.PipeConnect.ViewModels;

public static class FittingCardBuilder
{
    public static (List<FittingCardItem> Fittings, List<FittingCardItem> Reducers) Build(
        IReadOnlyList<FittingMappingRule> proposedFittings,
        ConnectionTypeCode staticCtc,
        ConnectionTypeCode dynamicCtc)
    {
        var fittings = new List<FittingCardItem>();
        var reducers = new List<FittingCardItem>();

        bool hasMandatoryFittings = proposedFittings.Count > 0 &&
            proposedFittings.Any(r => !r.IsDirectConnect && r.FittingFamilies.Count > 0);

        if (!hasMandatoryFittings)
            fittings.Add(new FittingCardItem(new FittingMappingRule
            {
                FromType = staticCtc,
                ToType = dynamicCtc,
                IsDirectConnect = true
            }));

        foreach (var rule in proposedFittings)
        {
            if (!rule.IsDirectConnect && rule.FittingFamilies.Count > 0)
            {
                foreach (var family in rule.FittingFamilies.OrderBy(f => f.Priority))
                    fittings.Add(new FittingCardItem(rule, family));
            }

            if (rule.ReducerFamilies.Count > 0)
            {
                foreach (var reducer in rule.ReducerFamilies.OrderBy(f => f.Priority))
                    reducers.Add(new FittingCardItem(rule, reducer, isReducer: true));
            }
        }

        return (fittings, reducers);
    }
}
