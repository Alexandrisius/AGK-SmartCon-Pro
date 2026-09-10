using SmartCon.Core.Math;
using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Math;

public sealed class TransitionSizeMatcherTests
{
    private static double Ft(double mm) => mm / 304.8;

    private static FamilySizeOption Option(
        string name, int targetConnIdx, double radiusMm,
        IReadOnlyDictionary<int, double>? allRadiiMm = null,
        bool isAutoSelect = false,
        string? symbolName = null,
        string? currentSymbolName = null)
    {
        var radii = new Dictionary<int, double>();
        if (allRadiiMm is not null)
            foreach (var kvp in allRadiiMm)
                radii[kvp.Key] = Ft(kvp.Value);

        return new FamilySizeOption
        {
            DisplayName = name,
            Radius = Ft(radiusMm),
            TargetConnectorIndex = targetConnIdx,
            AllConnectorRadii = radii,
            IsAutoSelect = isAutoSelect,
            SymbolName = symbolName,
            CurrentSymbolName = currentSymbolName,
        };
    }

    [Fact]
    public void FindBestTransition_ExactTransitionRow_PreferredOverUniform()
    {
        // Tee: conn1 = target (needs DN20), conn2 = DN25 currently.
        var current = new Dictionary<int, double> { [1] = Ft(25), [2] = Ft(25) };
        var candidates = new List<FamilySizeOption>
        {
            Option("DN 20 x DN 20", 1, 20, new Dictionary<int, double> { [1] = 20, [2] = 20 }),
            Option("DN 20 x DN 25", 1, 20, new Dictionary<int, double> { [1] = 20, [2] = 25 }),
        };

        var best = TransitionSizeMatcher.FindBestTransition(candidates, Ft(20), 1, current);

        Assert.NotNull(best);
        Assert.Equal("DN 20 x DN 25", best.DisplayName);
        Assert.Equal(0.0, TransitionSizeMatcher.OtherPortsDelta(best, 1, current), 9);
    }

    [Fact]
    public void FindBestTransition_NoMatchingTargetRadius_ReturnsNull()
    {
        var current = new Dictionary<int, double> { [1] = Ft(25) };
        var candidates = new List<FamilySizeOption>
        {
            Option("DN 25", 1, 25, new Dictionary<int, double> { [1] = 25 }),
        };

        Assert.Null(TransitionSizeMatcher.FindBestTransition(candidates, Ft(32), 1, current));
    }

    [Fact]
    public void FindBestTransition_AutoSelectIgnored()
    {
        var current = new Dictionary<int, double> { [1] = Ft(25) };
        var candidates = new List<FamilySizeOption>
        {
            Option("Auto", 1, 20, new Dictionary<int, double> { [1] = 20 }, isAutoSelect: true),
        };

        Assert.Null(TransitionSizeMatcher.FindBestTransition(candidates, Ft(20), 1, current));
    }

    [Fact]
    public void FindBestTransition_SymbolChangeSkipped()
    {
        var current = new Dictionary<int, double> { [1] = Ft(25) };
        var candidates = new List<FamilySizeOption>
        {
            Option("Other type", 1, 20, new Dictionary<int, double> { [1] = 20 },
                symbolName: "Type B", currentSymbolName: "Type A"),
        };

        Assert.Null(TransitionSizeMatcher.FindBestTransition(candidates, Ft(20), 1, current));
    }

    [Fact]
    public void FindBestTransition_ThreePortTee_MinimizesTotalChange()
    {
        // Tee with 3 ports: target conn1 DN20, conn2 DN25, conn3 DN32 should stay.
        var current = new Dictionary<int, double>
        {
            [1] = Ft(25), [2] = Ft(25), [3] = Ft(32),
        };
        var candidates = new List<FamilySizeOption>
        {
            Option("20x20x32", 1, 20, new Dictionary<int, double> { [1] = 20, [2] = 20, [3] = 32 }),
            Option("20x25x32", 1, 20, new Dictionary<int, double> { [1] = 20, [2] = 25, [3] = 32 }),
            Option("20x20x20", 1, 20, new Dictionary<int, double> { [1] = 20, [2] = 20, [3] = 20 }),
        };

        var best = TransitionSizeMatcher.FindBestTransition(candidates, Ft(20), 1, current);

        Assert.NotNull(best);
        Assert.Equal("20x25x32", best.DisplayName);
    }

    [Fact]
    public void OtherPortsDelta_CountsOnlyNonTargetPorts()
    {
        var option = Option("DN 20 x DN 32", 1, 20,
            new Dictionary<int, double> { [1] = 20, [2] = 32 });
        var current = new Dictionary<int, double> { [1] = Ft(25), [2] = Ft(25) };

        double delta = TransitionSizeMatcher.OtherPortsDelta(option, 1, current);

        Assert.Equal(Ft(7), delta, 6);
    }
}
