using SmartCon.Core.Services;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class RadiusSetIntersectorTests
{
    [Fact]
    public void Intersect_EmptyList_ReturnsEmpty()
    {
        var result = RadiusSetIntersector.Intersect([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Intersect_SingleSet_ReturnsSame()
    {
        var sets = new List<SortedSet<double>>
        {
            new() { 1.0, 2.0, 3.0 },
        };

        var result = RadiusSetIntersector.Intersect(sets);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Intersect_TwoOverlappingSets_ReturnsIntersection()
    {
        var sets = new List<SortedSet<double>>
        {
            new() { 1.0, 2.0, 3.0 },
            new() { 2.0, 3.0, 4.0 },
        };

        var result = RadiusSetIntersector.Intersect(sets);
        Assert.Equal(2, result.Count);
        Assert.Contains(2.0, result);
        Assert.Contains(3.0, result);
    }

    [Fact]
    public void Intersect_NoOverlap_ReturnsEmpty()
    {
        var sets = new List<SortedSet<double>>
        {
            new() { 1.0, 2.0 },
            new() { 3.0, 4.0 },
        };

        var result = RadiusSetIntersector.Intersect(sets);
        Assert.Empty(result);
    }

    [Fact]
    public void Intersect_WithinEpsilon_Matches()
    {
        var sets = new List<SortedSet<double>>
        {
            new() { 1.0 },
            new() { 1.0 + 5e-7 },
        };

        var result = RadiusSetIntersector.Intersect(sets);
        Assert.Single(result);
    }

    [Fact]
    public void Intersect_OutsideEpsilon_NoMatch()
    {
        var sets = new List<SortedSet<double>>
        {
            new() { 1.0 },
            new() { 1.0 + 1e-5 },
        };

        var result = RadiusSetIntersector.Intersect(sets);
        Assert.Empty(result);
    }

    [Fact]
    public void Intersect_ThreeSets_ReturnsCommon()
    {
        var sets = new List<SortedSet<double>>
        {
            new() { 1.0, 2.0, 3.0, 4.0 },
            new() { 2.0, 3.0, 4.0, 5.0 },
            new() { 3.0, 4.0, 5.0, 6.0 },
        };

        var result = RadiusSetIntersector.Intersect(sets);
        Assert.Equal(2, result.Count);
        Assert.Contains(3.0, result);
        Assert.Contains(4.0, result);
    }
}
