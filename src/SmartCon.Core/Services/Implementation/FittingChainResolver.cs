using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

public sealed class FittingChainResolver : IFittingChainResolver
{
    private const double RadiusEpsilon = 1e-6;

    private readonly IFittingMapper _fittingMapper;
    private readonly IFittingMappingRepository _mappingRepo;

    public FittingChainResolver(
        IFittingMapper fittingMapper,
        IFittingMappingRepository mappingRepo)
    {
        _fittingMapper = fittingMapper;
        _mappingRepo = mappingRepo;
    }

    public FittingChainPlan Resolve(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius)
    {
        bool sameCtc = staticCtc.Value == dynamicCtc.Value;
        bool sameDn = System.Math.Abs(staticRadius - dynamicRadius) < RadiusEpsilon;

        if (sameCtc && sameDn)
            return BuildDirectPlan(staticCtc, dynamicCtc, staticRadius, dynamicRadius);

        if (sameCtc && !sameDn)
            return BuildReducerOnlyPlan(staticCtc, staticRadius, dynamicRadius);

        if (!sameCtc && sameDn)
            return BuildFittingOnlyPlan(staticCtc, dynamicCtc, staticRadius)
                   ?? BuildDirectPlan(staticCtc, dynamicCtc, staticRadius, dynamicRadius);

        return ResolveMixed(staticCtc, dynamicCtc, staticRadius, dynamicRadius);
    }

    public IReadOnlyList<FittingChainPlan> ResolveAlternatives(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius,
        int maxAlternatives = 3)
    {
        var results = new List<FittingChainPlan>();
        bool sameCtc = staticCtc.Value == dynamicCtc.Value;
        bool sameDn = System.Math.Abs(staticRadius - dynamicRadius) < RadiusEpsilon;

        if (sameCtc && sameDn)
        {
            results.Add(BuildDirectPlan(staticCtc, dynamicCtc, staticRadius, dynamicRadius));
            return results;
        }

        if (sameCtc)
        {
            results.Add(BuildReducerOnlyPlan(staticCtc, staticRadius, dynamicRadius));
            return results;
        }

        if (sameDn)
        {
            var fittingPlan = BuildFittingOnlyPlan(staticCtc, dynamicCtc, staticRadius);
            if (fittingPlan is not null)
                results.Add(fittingPlan);
            return results;
        }

        var allCandidates = ResolveAllMixedCandidates(staticCtc, dynamicCtc, staticRadius, dynamicRadius);
        results.AddRange(allCandidates.Take(maxAlternatives));

        if (results.Count == 0)
            results.Add(BuildFittingOnlyPlan(staticCtc, dynamicCtc, staticRadius) ?? BuildDirectPlan(staticCtc, dynamicCtc, staticRadius, dynamicRadius));

        return results;
    }

    private FittingChainPlan ResolveMixed(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius)
    {
        var candidates = ResolveAllMixedCandidates(staticCtc, dynamicCtc, staticRadius, dynamicRadius);

        return candidates.Count > 0
            ? candidates[0]
            : BuildFittingOnlyPlan(staticCtc, dynamicCtc, staticRadius)
              ?? BuildDirectPlan(staticCtc, dynamicCtc, staticRadius, dynamicRadius);
    }

    private List<FittingChainPlan> ResolveAllMixedCandidates(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius)
    {
        var candidates = new List<FittingChainPlan>();
        var fittingRules = _fittingMapper.GetMappings(staticCtc, dynamicCtc);

        // Strategy A: single fitting covers both CTC and DN change
        var fittingOnly = TryBuildFittingCoversBothDn(staticCtc, dynamicCtc, staticRadius, dynamicRadius, fittingRules);
        if (fittingOnly is not null)
        {
            candidates.Add(fittingOnly);
            return candidates;
        }

        // Strategy B: fitting + reducer (existing working case)
        // static → fitting(staticCtc→dynamicCtc) → reducer(dynamicCtc) → dynamic
        var fittingReducer = TryBuildFittingReducerPlan(staticCtc, dynamicCtc, staticRadius, dynamicRadius, fittingRules);
        if (fittingReducer is not null)
            candidates.Add(fittingReducer);

        // Strategy C: reducer + fitting (NEW case)
        // static → reducer(staticCtc, staticDN→dynamicDN) → fitting(staticCtc→dynamicCtc at dynamicDN) → dynamic
        var reducerFitting = TryBuildReducerFittingPlan(staticCtc, dynamicCtc, staticRadius, dynamicRadius, fittingRules);
        if (reducerFitting is not null)
            candidates.Add(reducerFitting);

        // Sort: fewer elements first, then by total priority
        candidates.Sort((a, b) =>
        {
            int cmp = a.Links.Count.CompareTo(b.Links.Count);
            if (cmp != 0) return cmp;
            return a.Links.Sum(l => l.Family.Priority).CompareTo(b.Links.Sum(l => l.Family.Priority));
        });

        // TODO [ChainV2]: Strategy D — Dijkstra multi-hop (fitting1 + fitting2 + ... + reducer)
        // Если ни одна из стратегий B/C не дала результата, использовать FindShortestFittingPath()
        // для поиска цепочки через промежуточные CTC:
        // 1. Найти все промежуточные CTC через Дейкстру
        // 2. Для каждого перехода — подобрать фитинг
        // 3. Добавить reducer-ы для DN-расхождений между фитингами
        // 4. Собрать полный FittingChainPlan с topology = FittingChain / FittingChainReducer / ReducerFittingChain

        return candidates;
    }

    private FittingChainPlan BuildDirectPlan(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius)
    {
        return new FittingChainPlan
        {
            StaticCtc = staticCtc,
            DynamicCtc = dynamicCtc,
            StaticRadius = staticRadius,
            DynamicRadius = dynamicRadius,
            Topology = ChainTopology.Direct,
            Links = []
        };
    }

    private FittingChainPlan BuildReducerOnlyPlan(
        ConnectionTypeCode ctc, double staticRadius, double dynamicRadius)
    {
        var reducerRules = FindReducerRules(ctc);
        FittingMappingRule? bestRule = reducerRules.Count > 0 ? reducerRules[0] : null;
        var bestFamily = bestRule?.ReducerFamilies.Count > 0 ? bestRule!.ReducerFamilies[0] : null;

        if (bestRule is null || bestFamily is null)
        {
            var directRules = _fittingMapper.GetMappings(ctc, ctc);
            bestRule = directRules.Count > 0 ? directRules[0] : null;
            bestFamily = bestRule?.ReducerFamilies.Count > 0 ? bestRule!.ReducerFamilies[0] : null;
        }

        if (bestRule is null || bestFamily is null)
            return BuildDirectPlan(ctc, ctc, staticRadius, dynamicRadius);

        return new FittingChainPlan
        {
            StaticCtc = ctc,
            DynamicCtc = ctc,
            StaticRadius = staticRadius,
            DynamicRadius = dynamicRadius,
            Topology = ChainTopology.ReducerOnly,
            Links =
            [
                new FittingChainLink
                {
                    Type = FittingChainNodeType.Reducer,
                    Rule = bestRule,
                    Family = bestFamily,
                    CtcIn = ctc,
                    CtcOut = ctc,
                    RadiusIn = staticRadius,
                    RadiusOut = dynamicRadius
                }
            ]
        };
    }

    private FittingChainPlan? BuildFittingOnlyPlan(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double radius)
    {
        var rules = _fittingMapper.GetMappings(staticCtc, dynamicCtc);
        var bestRule = rules.FirstOrDefault(r => r.FittingFamilies.Count > 0 && !r.IsDirectConnect);

        if (bestRule is null)
        {
            var dijkstraPath = _fittingMapper.FindShortestFittingPath(staticCtc, dynamicCtc);
            bestRule = dijkstraPath.FirstOrDefault(r => r.FittingFamilies.Count > 0);
        }

        var bestFamily = bestRule?.FittingFamilies.FirstOrDefault();
        if (bestRule is null || bestFamily is null)
            return null;

        return new FittingChainPlan
        {
            StaticCtc = staticCtc,
            DynamicCtc = dynamicCtc,
            StaticRadius = radius,
            DynamicRadius = radius,
            Topology = ChainTopology.FittingOnly,
            Links =
            [
                new FittingChainLink
                {
                    Type = FittingChainNodeType.Fitting,
                    Rule = bestRule,
                    Family = bestFamily,
                    CtcIn = staticCtc,
                    CtcOut = dynamicCtc,
                    RadiusIn = radius,
                    RadiusOut = radius
                }
            ]
        };
    }

    private FittingChainPlan? TryBuildFittingCoversBothDn(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius,
        IReadOnlyList<FittingMappingRule> fittingRules)
    {
        // TODO [ChainV2]: Проверка реальной доступности типоразмеров фитинга с обоими DN
        // сейчас проверяем только наличие правила маппинга.
        // Для точной проверки нужен вызов Revit API (Revit-layer concern).
        // На уровне resolver считаем, что фитинг "может" покрыть оба DN если правило существует.
        return null;
    }

    private FittingChainPlan? TryBuildFittingReducerPlan(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius,
        IReadOnlyList<FittingMappingRule> fittingRules)
    {
        var fittingRule = fittingRules.FirstOrDefault(r => r.FittingFamilies.Count > 0 && !r.IsDirectConnect);

        if (fittingRule is null)
        {
            var dijkstraPath = _fittingMapper.FindShortestFittingPath(staticCtc, dynamicCtc);
            fittingRule = dijkstraPath.FirstOrDefault(r => r.FittingFamilies.Count > 0);
        }

        if (fittingRule is null)
            return null;

        var fittingFamily = fittingRule.FittingFamilies[0];

        var reducerRules = FindReducerRules(dynamicCtc);
        FittingMappingRule? reducerRule = reducerRules.Count > 0 ? reducerRules[0] : null;
        var reducerFamily = reducerRule?.ReducerFamilies.Count > 0 ? reducerRule!.ReducerFamilies[0] : null;

        if (reducerRule is null || reducerFamily is null)
        {
            var dcRules = _fittingMapper.GetMappings(dynamicCtc, dynamicCtc);
            reducerRule = dcRules.Count > 0 ? dcRules[0] : null;
            reducerFamily = reducerRule?.ReducerFamilies.Count > 0 ? reducerRule!.ReducerFamilies[0] : null;
        }

        if (reducerRule is null || reducerFamily is null)
            return null;

        return new FittingChainPlan
        {
            StaticCtc = staticCtc,
            DynamicCtc = dynamicCtc,
            StaticRadius = staticRadius,
            DynamicRadius = dynamicRadius,
            Topology = ChainTopology.FittingReducer,
            Links =
            [
                new FittingChainLink
                {
                    Type = FittingChainNodeType.Fitting,
                    Rule = fittingRule,
                    Family = fittingFamily,
                    CtcIn = staticCtc,
                    CtcOut = dynamicCtc,
                    RadiusIn = staticRadius,
                    RadiusOut = staticRadius
                },
                new FittingChainLink
                {
                    Type = FittingChainNodeType.Reducer,
                    Rule = reducerRule,
                    Family = reducerFamily,
                    CtcIn = dynamicCtc,
                    CtcOut = dynamicCtc,
                    RadiusIn = staticRadius,
                    RadiusOut = dynamicRadius
                }
            ]
        };
    }

    private FittingChainPlan? TryBuildReducerFittingPlan(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius,
        IReadOnlyList<FittingMappingRule> fittingRules)
    {
        // Reducer: staticCtc, staticRadius → staticCtc, dynamicRadius
        var reducerRules = FindReducerRules(staticCtc);
        FittingMappingRule? reducerRule = reducerRules.Count > 0 ? reducerRules[0] : null;
        var reducerFamily = reducerRule?.ReducerFamilies.Count > 0 ? reducerRule!.ReducerFamilies[0] : null;

        if (reducerRule is null || reducerFamily is null)
        {
            var dcRules = _fittingMapper.GetMappings(staticCtc, staticCtc);
            reducerRule = dcRules.Count > 0 ? dcRules[0] : null;
            reducerFamily = reducerRule?.ReducerFamilies.Count > 0 ? reducerRule!.ReducerFamilies[0] : null;
        }

        if (reducerRule is null || reducerFamily is null)
            return null;

        // Fitting: staticCtc → dynamicCtc at dynamicRadius
        var fittingRule = fittingRules.FirstOrDefault(r => r.FittingFamilies.Count > 0 && !r.IsDirectConnect);

        if (fittingRule is null)
        {
            var dijkstraPath = _fittingMapper.FindShortestFittingPath(staticCtc, dynamicCtc);
            fittingRule = dijkstraPath.FirstOrDefault(r => r.FittingFamilies.Count > 0);
        }

        if (fittingRule is null)
            return null;

        var fittingFamily = fittingRule.FittingFamilies[0];

        return new FittingChainPlan
        {
            StaticCtc = staticCtc,
            DynamicCtc = dynamicCtc,
            StaticRadius = staticRadius,
            DynamicRadius = dynamicRadius,
            Topology = ChainTopology.ReducerFitting,
            Links =
            [
                new FittingChainLink
                {
                    Type = FittingChainNodeType.Reducer,
                    Rule = reducerRule,
                    Family = reducerFamily,
                    CtcIn = staticCtc,
                    CtcOut = staticCtc,
                    RadiusIn = staticRadius,
                    RadiusOut = dynamicRadius
                },
                new FittingChainLink
                {
                    Type = FittingChainNodeType.Fitting,
                    Rule = fittingRule,
                    Family = fittingFamily,
                    CtcIn = staticCtc,
                    CtcOut = dynamicCtc,
                    RadiusIn = dynamicRadius,
                    RadiusOut = dynamicRadius
                }
            ]
        };
    }

    private IReadOnlyList<FittingMappingRule> FindReducerRules(ConnectionTypeCode ctc)
    {
        var allRules = _mappingRepo.GetMappingRules();
        return allRules
            .Where(r => r.FromType.Value == ctc.Value && r.ToType.Value == ctc.Value
                        && r.ReducerFamilies.Count > 0)
            .OrderBy(r => r.ReducerFamilies.Min(f => f.Priority))
            .ToList();
    }
}
