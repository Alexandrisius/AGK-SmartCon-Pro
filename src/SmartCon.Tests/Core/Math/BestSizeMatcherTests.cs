using SmartCon.Core.Math;
using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Math;

public sealed class BestSizeMatcherTests
{
    private static FamilySizeOption MakeOption(string name, double radius, int targetIdx = 1,
        Dictionary<int, double>? allRadii = null)
    {
        allRadii ??= new Dictionary<int, double> { [targetIdx] = radius };
        return new FamilySizeOption
        {
            DisplayName = name,
            Radius = radius,
            TargetConnectorIndex = targetIdx,
            AllConnectorRadii = allRadii,
        };
    }

    // ── FindClosestWeighted ──────────────────────────────────────────────

    [Fact]
    public void FindClosestWeighted_EmptyCandidates_ReturnsNull()
    {
        var result = BestSizeMatcher.FindClosestWeighted([], 0.01, 1, new Dictionary<int, double>());
        Assert.Null(result);
    }

    [Fact]
    public void FindClosestWeighted_SingleCandidate_ReturnsIt()
    {
        var opt = MakeOption("DN15", 0.0246);
        var result = BestSizeMatcher.FindClosestWeighted([opt], 0.025, 1, new Dictionary<int, double> { [1] = 0.025 });
        Assert.Same(opt, result);
    }

    [Fact]
    public void FindClosestWeighted_PicksClosestByTargetRadius()
    {
        var dn15 = MakeOption("DN15", 0.0246);
        var dn20 = MakeOption("DN20", 0.0328);
        var dn25 = MakeOption("DN25", 0.041);

        var result = BestSizeMatcher.FindClosestWeighted(
            [dn15, dn20, dn25], targetRadius: 0.033, targetConnIdx: 1,
            currentRadii: new Dictionary<int, double> { [1] = 0.033 });

        Assert.Equal("DN20", result!.DisplayName);
    }

    [Fact]
    public void FindClosestWeighted_OtherConnectorsBreakTie()
    {
        // Two options with same target radius but different other-connector radii
        var optA = MakeOption("A", 0.025, 1, new Dictionary<int, double> { [1] = 0.025, [2] = 0.015 });
        var optB = MakeOption("B", 0.025, 1, new Dictionary<int, double> { [1] = 0.025, [2] = 0.030 });

        var currentRadii = new Dictionary<int, double> { [1] = 0.025, [2] = 0.016 };

        var result = BestSizeMatcher.FindClosestWeighted([optA, optB], 0.025, 1, currentRadii);
        Assert.Equal("A", result!.DisplayName); // optA has closer other-connector match
    }

    [Fact]
    public void FindClosestWeighted_TargetWeightDominates()
    {
        // optA: target delta = 0.005 (weighted 3x = 0.015), other delta = 0
        var optA = MakeOption("A", 0.030, 1, new Dictionary<int, double> { [1] = 0.030, [2] = 0.020 });
        // optB: target delta = 0.001 (weighted 3x = 0.003), other delta = 0.010
        var optB = MakeOption("B", 0.026, 1, new Dictionary<int, double> { [1] = 0.026, [2] = 0.030 });

        var currentRadii = new Dictionary<int, double> { [1] = 0.025, [2] = 0.020 };

        var result = BestSizeMatcher.FindClosestWeighted([optA, optB], 0.025, 1, currentRadii);
        Assert.Equal("B", result!.DisplayName); // B has lower total: 0.003 + 0.010 = 0.013 vs A: 0.015 + 0 = 0.015
    }

    // ── FindClosestByRadius ─────────────────────────────────────────────

    [Fact]
    public void FindClosestByRadius_EmptyCandidates_ReturnsNull()
    {
        var result = BestSizeMatcher.FindClosestByRadius([], 0.01, 1, new Dictionary<int, double>());
        Assert.Null(result);
    }

    [Fact]
    public void FindClosestByRadius_PicksExactMatch()
    {
        var dn15 = MakeOption("DN15", 0.0246);
        var dn20 = MakeOption("DN20", 0.0328);

        var result = BestSizeMatcher.FindClosestByRadius(
            [dn15, dn20], targetRadius: 0.0328, targetConnIdx: 1,
            currentRadii: new Dictionary<int, double> { [1] = 0.0328 });

        Assert.Equal("DN20", result!.DisplayName);
    }

    [Fact]
    public void FindClosestByRadius_TieBreaksOnOtherDeltas()
    {
        // Both have same radius for target connector
        var optA = MakeOption("A", 0.025, 1, new Dictionary<int, double> { [1] = 0.025, [2] = 0.015 });
        var optB = MakeOption("B", 0.025, 1, new Dictionary<int, double> { [1] = 0.025, [2] = 0.020 });

        var currentRadii = new Dictionary<int, double> { [1] = 0.025, [2] = 0.020 };

        var result = BestSizeMatcher.FindClosestByRadius([optA, optB], 0.025, 1, currentRadii);
        Assert.Equal("B", result!.DisplayName); // B has exact match on connector 2
    }
}
