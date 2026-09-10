using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Selection;

namespace SmartCon.IntegrationTests.PipeConnect;

/// <summary>
/// ElementChainIterator: BFS-обход реальной сети соединённых труб.
/// Проверяется только руками до этого набора — ядро Chain-режима PipeConnect.
/// </summary>
public sealed class ElementChainIteratorTests : PipeModelFixture
{
    private ElementChainIterator? _iterator;
    private ElementChainIterator Iterator => _iterator!;

    [Before(Test)]
    public void CreateIterator()
    {
        _iterator = new ElementChainIterator();
    }

    [Test]
    public async Task BuildGraph_FullChainOfThree_TraversesAllLevels()
    {
        // Arrange — линейная сеть A—B—C
        ConnectPipesAt(FirstPipeId, SecondPipeId, P1);
        ConnectPipesAt(SecondPipeId, ThirdPipeId, P2);

        // Act
        var graph = Iterator.BuildGraph(Doc, FirstPipeId);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(graph.Nodes.Count).IsEqualTo(3);
            await Assert.That(graph.TotalChainElements).IsEqualTo(2);
            await Assert.That(graph.MaxLevel).IsEqualTo(2);
            await Assert.That(graph.Edges.Count).IsEqualTo(2);
            await Assert.That(graph.ContainsMepCurve(SecondPipeId)).IsTrue();
        }
    }

    [Test]
    public async Task BuildGraph_StopAtMiddlePipe_ExcludesDownstream()
    {
        // Arrange
        ConnectPipesAt(FirstPipeId, SecondPipeId, P1);
        ConnectPipesAt(SecondPipeId, ThirdPipeId, P2);

        // Act — обход со стоп-элементом: сеть за ним не траверсится
        var graph = Iterator.BuildGraph(Doc, FirstPipeId, [SecondPipeId]);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(graph.Nodes.Count).IsEqualTo(1);
            await Assert.That(graph.TotalChainElements).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetChainEndConnectors_TwoConnectedPipes_ReturnsTwoFreeEnds()
    {
        // Arrange — соединены только A—B: свободны начало A и конец B
        ConnectPipesAt(FirstPipeId, SecondPipeId, P1);
        var graph = Iterator.BuildGraph(Doc, FirstPipeId);

        // Act
        var ends = Iterator.GetChainEndConnectors(Doc, graph);

        // Assert — дальние концы цепочки: (0,0,0) и (20,0,0)
        using (Assert.Multiple())
        {
            await Assert.That(ends.Count).IsEqualTo(2);
            await Assert.That(ends.All(e => e.IsFree)).IsTrue();
            await Assert.That(ends.Any(e => e.Origin.X < 1.0)).IsTrue();
            await Assert.That(ends.Any(e => e.Origin.X > 19.0)).IsTrue();
        }
    }
}
