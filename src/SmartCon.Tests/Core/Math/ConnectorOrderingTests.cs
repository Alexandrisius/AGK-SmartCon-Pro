using SmartCon.Core.Math;
using Xunit;

namespace SmartCon.Tests.Core.Math;

public sealed class ConnectorOrderingTests
{
    private sealed record TestItem(Vec3 Position, int Id);

    private static IReadOnlyList<TestItem> Order(IEnumerable<TestItem> items)
        => ConnectorOrdering.OrderByPosition(items, i => i.Position, i => i.Id);

    [Fact]
    public void OrderByPosition_SortsByXAscending()
    {
        var items = new[]
        {
            new TestItem(new Vec3(2, 0, 0), 1),
            new TestItem(new Vec3(-1, 0, 0), 2),
            new TestItem(new Vec3(0, 0, 0), 3),
        };

        var result = Order(items);

        Assert.Equal(new[] { 2, 3, 1 }, result.Select(i => i.Id));
    }

    [Fact]
    public void OrderByPosition_EqualX_SortsByZDescending()
    {
        var items = new[]
        {
            new TestItem(new Vec3(0, 0, 1), 1),
            new TestItem(new Vec3(0, 0, 3), 2),
            new TestItem(new Vec3(0, 0, 2), 3),
        };

        var result = Order(items);

        Assert.Equal(new[] { 2, 3, 1 }, result.Select(i => i.Id));
    }

    [Fact]
    public void OrderByPosition_EqualXAndZ_SortsByYAscending()
    {
        var items = new[]
        {
            new TestItem(new Vec3(0, 5, 1), 1),
            new TestItem(new Vec3(0, -2, 1), 2),
            new TestItem(new Vec3(0, 0, 1), 3),
        };

        var result = Order(items);

        Assert.Equal(new[] { 2, 3, 1 }, result.Select(i => i.Id));
    }

    [Fact]
    public void OrderByPosition_AllAxesEqual_TieBreaksById()
    {
        var items = new[]
        {
            new TestItem(new Vec3(1, 2, 3), 30),
            new TestItem(new Vec3(1, 2, 3), 10),
            new TestItem(new Vec3(1, 2, 3), 20),
        };

        var result = Order(items);

        Assert.Equal(new[] { 10, 20, 30 }, result.Select(i => i.Id));
    }

    [Fact]
    public void OrderByPosition_XWinsOverZAndY()
    {
        var items = new[]
        {
            new TestItem(new Vec3(1, -100, -100), 1),
            new TestItem(new Vec3(0, 100, 100), 2),
        };

        var result = Order(items);

        Assert.Equal(new[] { 2, 1 }, result.Select(i => i.Id));
    }

    [Fact]
    public void OrderByPosition_WithinTolerance_TreatedAsEqualAndFallsToNextAxis()
    {
        double baseX = 10.0;
        double tinyDelta = ConnectorOrdering.PositionTolerance / 2;

        var items = new[]
        {
            new TestItem(new Vec3(baseX, 0, 0), 1),
            new TestItem(new Vec3(baseX + tinyDelta, 0, 5), 2),
        };

        var result = Order(items);

        Assert.Equal(new[] { 2, 1 }, result.Select(i => i.Id));
    }

    [Fact]
    public void OrderByPosition_OutsideTolerance_TreatedAsDistinct()
    {
        double baseX = 10.0;
        double delta = ConnectorOrdering.PositionTolerance * 2;

        var items = new[]
        {
            new TestItem(new Vec3(baseX + delta, 0, 5), 2),
            new TestItem(new Vec3(baseX, 0, 0), 1),
        };

        var result = Order(items);

        Assert.Equal(new[] { 1, 2 }, result.Select(i => i.Id));
    }

    [Fact]
    public void OrderByPosition_ShuffledInput_ProducesIdenticalOrder()
    {
        var reference = new[]
        {
            new TestItem(new Vec3(-1, 0, 0), 5),
            new TestItem(new Vec3(0, 0, 2), 1),
            new TestItem(new Vec3(0, 0, 2), 3),
            new TestItem(new Vec3(0, 1, 2), 4),
            new TestItem(new Vec3(2, 0, -7), 2),
        };

        var expected = Order(reference).Select(i => i.Id).ToArray();

        for (int seed = 0; seed < 10; seed++)
        {
            var shuffled = reference.OrderBy(_ => new Random(seed).Next()).ToArray();
            var actual = Order(shuffled).Select(i => i.Id).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void OrderByPosition_EmptyInput_ReturnsEmpty()
    {
        var result = Order(Array.Empty<TestItem>());

        Assert.Empty(result);
    }

    [Fact]
    public void OrderByPosition_SingleItem_ReturnsSameItem()
    {
        var items = new[] { new TestItem(new Vec3(1, 2, 3), 7) };

        var result = Order(items);

        Assert.Single(result);
        Assert.Equal(7, result[0].Id);
    }

    [Fact]
    public void OrderByPosition_DoesNotMutateInputList()
    {
        var items = new List<TestItem>
        {
            new(new Vec3(2, 0, 0), 1),
            new(new Vec3(0, 0, 0), 2),
        };

        Order(items);

        Assert.Equal(1, items[0].Id);
        Assert.Equal(2, items[1].Id);
    }

    [Fact]
    public void OrderByPosition_NullItems_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ConnectorOrdering.OrderByPosition<TestItem>(null!, i => i.Position, i => i.Id));
    }

    [Fact]
    public void OrderByPosition_NullPositionSelector_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ConnectorOrdering.OrderByPosition(Array.Empty<TestItem>(), null!, i => i.Id));
    }

    [Fact]
    public void OrderByPosition_NullTieBreakerSelector_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ConnectorOrdering.OrderByPosition(Array.Empty<TestItem>(), i => i.Position, null!));
    }

    [Fact]
    public void OrderByPosition_ManifoldScenario_PortsStayInGeometricOrder()
    {
        var threePorts = new[]
        {
            new TestItem(new Vec3(0, 0, 1), 101),
            new TestItem(new Vec3(0, 0, 0), 100),
            new TestItem(new Vec3(0, 0, 2), 102),
        };

        var firstOrder = Order(threePorts).Select(i => i.Id).ToArray();
        Assert.Equal(new[] { 102, 101, 100 }, firstOrder);

        var fivePorts = threePorts.Concat(new[]
        {
            new TestItem(new Vec3(0, 0, 3), 103),
            new TestItem(new Vec3(0, 0, -1), 99),
        });

        var grownOrder = Order(fivePorts).Select(i => i.Id).ToArray();
        Assert.Equal(new[] { 103, 102, 101, 100, 99 }, grownOrder);
    }
}
