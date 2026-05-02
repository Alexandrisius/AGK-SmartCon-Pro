using SmartCon.Core.Models;
using SmartCon.PipeConnect.ViewModels;
using Xunit;

namespace SmartCon.Tests.PipeConnect;

public sealed class FittingCardBuilderTests
{
    private static readonly ConnectionTypeCode Fl = ConnectionTypeCode.Parse("FL");
    private static readonly ConnectionTypeCode Pn = ConnectionTypeCode.Parse("PN");
    private static readonly ConnectionTypeCode Wd = ConnectionTypeCode.Parse("WD");

    private static FittingMappingRule MakeRule(
        ConnectionTypeCode from, ConnectionTypeCode to,
        bool isDirectConnect = false,
        string[]? fittingFamilies = null,
        string[]? reducerFamilies = null)
    {
        var fittings = fittingFamilies?
            .Select((f, i) => new FittingMapping { FamilyName = f, SymbolName = "*", Priority = i })
            .ToList() ?? [];

        var reducers = reducerFamilies?
            .Select((f, i) => new FittingMapping { FamilyName = f, SymbolName = "*", Priority = i })
            .ToList() ?? [];

        return new FittingMappingRule
        {
            FromType = from,
            ToType = to,
            IsDirectConnect = isDirectConnect,
            FittingFamilies = fittings,
            ReducerFamilies = reducers,
        };
    }

    [Fact]
    public void Build_EmptyRules_CreatesDirectConnect()
    {
        var (fittings, reducers) = FittingCardBuilder.Build([], Fl, Pn);

        Assert.Single(fittings);
        Assert.True(fittings[0].IsDirectConnect);
        Assert.Empty(reducers);
    }

    [Fact]
    public void Build_DirectConnectOnly_CreatesDirectConnect()
    {
        var rules = new List<FittingMappingRule>
        {
            MakeRule(Fl, Pn, isDirectConnect: true),
        };

        var (fittings, reducers) = FittingCardBuilder.Build(rules, Fl, Pn);

        Assert.Single(fittings);
        Assert.True(fittings[0].IsDirectConnect);
        Assert.Empty(reducers);
    }

    [Fact]
    public void Build_WithFittingFamilies_CreatesFittingCards()
    {
        var rules = new List<FittingMappingRule>
        {
            MakeRule(Fl, Pn, fittingFamilies: ["Elbow_90", "Elbow_45"]),
        };

        var (fittings, reducers) = FittingCardBuilder.Build(rules, Fl, Pn);

        Assert.Equal(2, fittings.Count);
        Assert.False(fittings[0].IsDirectConnect);
        Assert.Equal("Elbow_90", fittings[0].PrimaryFitting!.FamilyName);
        Assert.Equal("Elbow_45", fittings[1].PrimaryFitting!.FamilyName);
        Assert.Empty(reducers);
    }

    [Fact]
    public void Build_WithReducerFamilies_CreatesReducerCards()
    {
        var rules = new List<FittingMappingRule>
        {
            MakeRule(Fl, Pn, fittingFamilies: ["Elbow"], reducerFamilies: ["Reducer_A", "Reducer_B"]),
        };

        var (fittings, reducers) = FittingCardBuilder.Build(rules, Fl, Pn);

        Assert.Single(fittings);
        Assert.Equal(2, reducers.Count);
        Assert.True(reducers[0].IsReducer);
        Assert.Equal("Reducer_A", reducers[0].PrimaryFitting!.FamilyName);
    }

    [Fact]
    public void Build_FittingFamilies_OrderedByPriority()
    {
        var rules = new List<FittingMappingRule>
        {
            new()
            {
                FromType = Fl,
                ToType = Pn,
                FittingFamilies =
                [
                    new FittingMapping { FamilyName = "B_Fit", SymbolName = "*", Priority = 2 },
                    new FittingMapping { FamilyName = "A_Fit", SymbolName = "*", Priority = 1 },
                ],
            },
        };

        var (fittings, _) = FittingCardBuilder.Build(rules, Fl, Pn);

        Assert.Equal("A_Fit", fittings[0].PrimaryFitting!.FamilyName);
        Assert.Equal("B_Fit", fittings[1].PrimaryFitting!.FamilyName);
    }

    [Fact]
    public void Build_MixedRules_OnlyFittingCardAdded()
    {
        var rules = new List<FittingMappingRule>
        {
            MakeRule(Fl, Pn, isDirectConnect: true),
            MakeRule(Fl, Wd, fittingFamilies: ["Tee"]),
        };

        var (fittings, _) = FittingCardBuilder.Build(rules, Fl, Pn);

        Assert.Single(fittings);
        Assert.False(fittings[0].IsDirectConnect);
        Assert.Equal("Tee", fittings[0].PrimaryFitting!.FamilyName);
    }

    [Fact]
    public void Build_RuleWithEmptyFittingFamilies_AddsDirectConnect()
    {
        var rules = new List<FittingMappingRule>
        {
            MakeRule(Fl, Pn, fittingFamilies: [], reducerFamilies: ["Reducer"]),
        };

        var (fittings, reducers) = FittingCardBuilder.Build(rules, Fl, Pn);

        Assert.Single(fittings);
        Assert.True(fittings[0].IsDirectConnect);
        Assert.Single(reducers);
    }
}
