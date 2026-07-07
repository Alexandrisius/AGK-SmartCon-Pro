namespace SmartCon.Tests.FamilyManager.Behaviors;

using SmartCon.FamilyManager.Behaviors;
using Xunit;

public sealed class StickyCascadingStackBuilderTests
{
    [Fact]
    public void BuildCascadingStack_NoScrolledItems_ReturnsEmpty()
    {
        var tops = new[] { 10.0, 50.0, 100.0 };
        var heights = new[] { 24.0, 24.0, 24.0 };
        var parents = new[] { -1, 0, 1 };

        var result = StickyCascadingStackBuilder.BuildCascadingStack(tops, heights, parents, viewportHeight: 200);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildCascadingStack_SingleScrolledItem_ReturnsPathToRoot()
    {
        // Root scrolled off, A and B scrolled off deeper.
        var tops = new[] { -10.0, -30.0, -60.0 };
        var heights = new[] { 24.0, 24.0, 24.0 };
        var parents = new[] { -1, 0, 1 };

        var result = StickyCascadingStackBuilder.BuildCascadingStack(tops, heights, parents, viewportHeight: 200);

        Assert.Equal(new[] { 0, 1, 2 }, result);
    }

    [Fact]
    public void BuildCascadingStack_OnlyRootScrolledOff_ReturnsOnlyRoot()
    {
        // Root scrolled off, child still visible below sticky bar.
        var tops = new[] { -10.0, 50.0 };
        var heights = new[] { 24.0, 24.0 };
        var parents = new[] { -1, 0 };

        var result = StickyCascadingStackBuilder.BuildCascadingStack(tops, heights, parents, viewportHeight: 200);

        Assert.Equal(new[] { 0 }, result);
    }

    [Fact]
    public void BuildCascadingStack_SiblingComesUp_IncludesSibling()
    {
        // Root and A scrolled off and pinned; B (sibling of A) now comes up under them.
        var tops = new[] { -50.0, -80.0, -100.0 };
        var heights = new[] { 24.0, 24.0, 24.0 };
        var parents = new[] { -1, 0, 0 };

        var result = StickyCascadingStackBuilder.BuildCascadingStack(tops, heights, parents, viewportHeight: 200);

        // Root pinned first, then A, then B pushes A? Actually Root+A+root path for B = [0] already, add [2].
        Assert.Equal(new[] { 0, 1, 2 }, result);
    }

    [Fact]
    public void BuildCascadingStack_OccupiedHeightReachesViewport_Stops()
    {
        // Many categories, but viewport is small — stack stops when occupied top exceeds viewport.
        var tops = new[] { -10.0, -40.0, -70.0, -100.0 };
        var heights = new[] { 30.0, 30.0, 30.0, 30.0 };
        var parents = new[] { -1, 0, 1, 2 };

        var result = StickyCascadingStackBuilder.BuildCascadingStack(tops, heights, parents, viewportHeight: 50);

        // Root (30) fits; next adds child (30) -> occupied=60 > viewport, loop condition stops.
        // Actually after adding root, occupied=30. Next candidate is index 1 (top=-40 < 30). Path [0,1] adds only 1 (30) -> occupied=60. Loop condition occupied < 50 false, break. Result [0,1].
        Assert.Equal(new[] { 0, 1 }, result);
    }

    [Fact]
    public void BuildCascadingStack_IgnoresItemsBelowViewport()
    {
        var tops = new[] { -10.0, 500.0 };
        var heights = new[] { 24.0, 24.0 };
        var parents = new[] { -1, 0 };

        var result = StickyCascadingStackBuilder.BuildCascadingStack(tops, heights, parents, viewportHeight: 200);

        Assert.Equal(new[] { 0 }, result);
    }

    [Fact]
    public void BuildCascadingStack_IgnoresUnknownTops()
    {
        var tops = new[] { -10.0, double.PositiveInfinity };
        var heights = new[] { 24.0, 24.0 };
        var parents = new[] { -1, 0 };

        var result = StickyCascadingStackBuilder.BuildCascadingStack(tops, heights, parents, viewportHeight: 200);

        Assert.Equal(new[] { 0 }, result);
    }

    [Fact]
    public void BuildCascadingStack_ThrowsWhenLengthsDiffer()
    {
        Assert.Throws<ArgumentException>(() =>
            StickyCascadingStackBuilder.BuildCascadingStack(
                new[] { 0.0, 0.0 },
                new[] { 24.0 },
                new[] { -1, -1 },
                200));
    }
}
