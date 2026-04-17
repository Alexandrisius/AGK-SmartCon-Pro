using System.IO;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class FittingChainResolverTests : IDisposable
{
    private readonly string _tempFile;
    private readonly JsonFittingMappingRepository _repository;

    public FittingChainResolverTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"smartcon-chain-{Guid.NewGuid()}.json");
        _repository = new JsonFittingMappingRepository(_tempFile);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    private (IFittingMapper mapper, IFittingChainResolver resolver) CreateServices(params FittingMappingRule[] rules)
    {
        _repository.SaveMappingRules(rules.ToList());
        var mapper = new FittingMapper(_repository);
        var resolver = new FittingChainResolver(mapper, _repository);
        return (mapper, resolver);
    }

    private static ConnectionTypeCode Ctc(int v) => new(v);

    // ── Direct ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_SameCtc_SameRadius_ReturnsDirect()
    {
        var (_, resolver) = CreateServices();
        var plan = resolver.Resolve(Ctc(1), Ctc(1), 0.1, 0.1);

        Assert.Equal(ChainTopology.Direct, plan.Topology);
        Assert.Empty(plan.Links);
        Assert.True(plan.IsDirect);
    }

    // ── ReducerOnly ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_SameCtc_DifferentRadius_WithReducerRule_ReturnsReducerOnly()
    {
        var (_, resolver) = CreateServices(
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(1),
                IsDirectConnect = true,
                FittingFamilies = [],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Переход", Priority = 1 },
                ],
            });

        var plan = resolver.Resolve(Ctc(1), Ctc(1), 0.1, 0.05);

        Assert.Equal(ChainTopology.ReducerOnly, plan.Topology);
        Assert.Single(plan.Links);
        Assert.Equal(FittingChainNodeType.Reducer, plan.Links[0].Type);
        Assert.Equal(0.1, plan.Links[0].RadiusIn);
        Assert.Equal(0.05, plan.Links[0].RadiusOut);
    }

    [Fact]
    public void Resolve_SameCtc_DifferentRadius_NoReducerRule_ReturnsDirect()
    {
        var (_, resolver) = CreateServices();
        var plan = resolver.Resolve(Ctc(1), Ctc(1), 0.1, 0.05);

        Assert.Equal(ChainTopology.Direct, plan.Topology);
    }

    // ── FittingOnly ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_DifferentCtc_SameRadius_ReturnsFittingOnly()
    {
        var (_, resolver) = CreateServices(
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Переходник", Priority = 1 },
                ],
            });

        var plan = resolver.Resolve(Ctc(1), Ctc(2), 0.1, 0.1);

        Assert.Equal(ChainTopology.FittingOnly, plan.Topology);
        Assert.Single(plan.Links);
        Assert.Equal(FittingChainNodeType.Fitting, plan.Links[0].Type);
        Assert.Equal(Ctc(1), plan.Links[0].CtcIn);
        Assert.Equal(Ctc(2), plan.Links[0].CtcOut);
    }

    // ── FittingReducer (existing case) ──────────────────────────────────

    [Fact]
    public void Resolve_DifferentCtc_DifferentRadius_WithFittingAndReducer_ReturnsFittingReducer()
    {
        var (_, resolver) = CreateServices(
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Фитинг", Priority = 1 },
                ],
            },
            new FittingMappingRule
            {
                FromType = Ctc(2), ToType = Ctc(2),
                IsDirectConnect = true,
                FittingFamilies = [],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Редьюсер", Priority = 1 },
                ],
            });

        var plan = resolver.Resolve(Ctc(1), Ctc(2), 0.1, 0.05);

        Assert.Equal(ChainTopology.FittingReducer, plan.Topology);
        Assert.Equal(2, plan.Links.Count);
        Assert.Equal(FittingChainNodeType.Fitting, plan.Links[0].Type);
        Assert.Equal(FittingChainNodeType.Reducer, plan.Links[1].Type);
        Assert.Equal("Фитинг", plan.Links[0].Family.FamilyName);
        Assert.Equal("Редьюсер", plan.Links[1].Family.FamilyName);
    }

    // ── ReducerFitting (NEW case) ───────────────────────────────────────

    [Fact]
    public void Resolve_DifferentCtc_DifferentRadius_WithReducerForStaticAndFitting_ReturnsReducerFitting()
    {
        var (_, resolver) = CreateServices(
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(1),
                IsDirectConnect = true,
                FittingFamilies = [],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Редьюсер", Priority = 1 },
                ],
            },
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Фитинг", Priority = 1 },
                ],
            });

        var plan = resolver.Resolve(Ctc(1), Ctc(2), 0.1, 0.05);

        Assert.Equal(ChainTopology.ReducerFitting, plan.Topology);
        Assert.Equal(2, plan.Links.Count);
        Assert.Equal(FittingChainNodeType.Reducer, plan.Links[0].Type);
        Assert.Equal(FittingChainNodeType.Fitting, plan.Links[1].Type);
        Assert.Equal("Редьюсер", plan.Links[0].Family.FamilyName);
        Assert.Equal("Фитинг", plan.Links[1].Family.FamilyName);
        Assert.Equal(Ctc(1), plan.Links[0].CtcIn);
        Assert.Equal(Ctc(1), plan.Links[0].CtcOut);
        Assert.Equal(Ctc(1), plan.Links[1].CtcIn);
        Assert.Equal(Ctc(2), plan.Links[1].CtcOut);
    }

    [Fact]
    public void Resolve_ReducerFitting_RadiiCorrect()
    {
        var (_, resolver) = CreateServices(
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(1),
                IsDirectConnect = true,
                FittingFamilies = [],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Редьюсер", Priority = 1 },
                ],
            },
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Фитинг", Priority = 1 },
                ],
            });

        double staticR = 0.1;
        double dynR = 0.05;
        var plan = resolver.Resolve(Ctc(1), Ctc(2), staticR, dynR);

        Assert.Equal(ChainTopology.ReducerFitting, plan.Topology);

        // Reducer: staticR → dynR
        Assert.Equal(staticR, plan.Links[0].RadiusIn);
        Assert.Equal(dynR, plan.Links[0].RadiusOut);

        // Fitting: dynR → dynR (already at correct DN after reducer)
        Assert.Equal(dynR, plan.Links[1].RadiusIn);
        Assert.Equal(dynR, plan.Links[1].RadiusOut);
    }

    // ── Both strategies available → fewer elements wins ─────────────────

    [Fact]
    public void Resolve_BothFittingReducerAndReducerFittingAvailable_PicksFewerElements()
    {
        var (_, resolver) = CreateServices(
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Фитинг", Priority = 1 },
                ],
            },
            new FittingMappingRule
            {
                FromType = Ctc(2), ToType = Ctc(2),
                IsDirectConnect = true,
                FittingFamilies = [],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Редьюсер2", Priority = 1 },
                ],
            },
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(1),
                IsDirectConnect = true,
                FittingFamilies = [],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Редьюсер1", Priority = 1 },
                ],
            });

        var plan = resolver.Resolve(Ctc(1), Ctc(2), 0.1, 0.05);

        Assert.Equal(2, plan.Links.Count);
        Assert.True(plan.Topology == ChainTopology.FittingReducer || plan.Topology == ChainTopology.ReducerFitting);
    }

    // ── No fitting rules → fallback ─────────────────────────────────────

    [Fact]
    public void Resolve_DifferentCtc_NoFittingRules_ReturnsDirect()
    {
        var (_, resolver) = CreateServices();
        var plan = resolver.Resolve(Ctc(1), Ctc(2), 0.1, 0.1);

        Assert.Equal(ChainTopology.Direct, plan.Topology);
    }

    // ── ResolveAlternatives ─────────────────────────────────────────────

    [Fact]
    public void ResolveAlternatives_ReturnsMultiplePlans()
    {
        var (_, resolver) = CreateServices(
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(2),
                IsDirectConnect = false,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "Фитинг", Priority = 1 },
                ],
            },
            new FittingMappingRule
            {
                FromType = Ctc(2), ToType = Ctc(2),
                IsDirectConnect = true,
                FittingFamilies = [],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Редьюсер2", Priority = 1 },
                ],
            },
            new FittingMappingRule
            {
                FromType = Ctc(1), ToType = Ctc(1),
                IsDirectConnect = true,
                FittingFamilies = [],
                ReducerFamilies =
                [
                    new FittingMapping { FamilyName = "Редьюсер1", Priority = 2 },
                ],
            });

        var alternatives = resolver.ResolveAlternatives(Ctc(1), Ctc(2), 0.1, 0.05);

        Assert.Equal(2, alternatives.Count);
        Assert.Equal(2, alternatives[0].Links.Count);
        Assert.Equal(2, alternatives[1].Links.Count);
        Assert.NotEqual(alternatives[0].Topology, alternatives[1].Topology);
    }

    [Fact]
    public void ResolveAlternatives_Direct_ReturnsSingle()
    {
        var (_, resolver) = CreateServices();
        var alternatives = resolver.ResolveAlternatives(Ctc(1), Ctc(1), 0.1, 0.1);

        Assert.Single(alternatives);
        Assert.Equal(ChainTopology.Direct, alternatives[0].Topology);
    }

    // ── FittingChainPlan properties ─────────────────────────────────────

    [Fact]
    public void FittingChainPlan_PropertiesCorrect()
    {
        var plan = new FittingChainPlan
        {
            StaticCtc = Ctc(1),
            DynamicCtc = Ctc(2),
            StaticRadius = 0.1,
            DynamicRadius = 0.05,
            Topology = ChainTopology.ReducerFitting,
            Links =
            [
                new FittingChainLink
                {
                    Type = FittingChainNodeType.Reducer,
                    Rule = new FittingMappingRule { FromType = Ctc(1), ToType = Ctc(1), IsDirectConnect = true },
                    Family = new FittingMapping { FamilyName = "R", Priority = 1 },
                    CtcIn = Ctc(1), CtcOut = Ctc(1),
                    RadiusIn = 0.1, RadiusOut = 0.05,
                },
                new FittingChainLink
                {
                    Type = FittingChainNodeType.Fitting,
                    Rule = new FittingMappingRule { FromType = Ctc(1), ToType = Ctc(2), IsDirectConnect = false },
                    Family = new FittingMapping { FamilyName = "F", Priority = 1 },
                    CtcIn = Ctc(1), CtcOut = Ctc(2),
                    RadiusIn = 0.05, RadiusOut = 0.05,
                },
            ],
        };

        Assert.False(plan.IsDirect);
        Assert.True(plan.HasReducer);
        Assert.Equal(1, plan.FittingCount);
        Assert.Equal(1, plan.ReducerCount);
    }
}
