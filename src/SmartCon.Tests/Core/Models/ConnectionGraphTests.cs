using Autodesk.Revit.DB;
using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

/// <summary>
/// Тесты ConnectionGraph требуют Revit runtime (ElementId имеет нативные зависимости).
/// Запуск: dotnet test --filter "Category=RevitRequired" внутри процесса Revit.
/// </summary>
[Trait("Category", "RevitRequired")]
public sealed class ConnectionGraphTests
{
    [Fact]
    public void NewGraph_ContainsRootNode()
    {
        var rootId = new ElementId(100L);
        var graph = new ConnectionGraph(rootId);

        Assert.Equal(rootId.Value, graph.RootId.Value);
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void GetChainFrom_RootOnly_ReturnsRoot()
    {
        var rootId = new ElementId(1L);
        var graph = new ConnectionGraph(rootId);

        var chain = graph.GetChainFrom(rootId).ToList();

        Assert.Single(chain);
        Assert.Equal(rootId.Value, chain[0].Value);
    }

    [Fact]
    public void GetChainFrom_LinearChain_ReturnsAllNodes()
    {
        // Строим линейную цепочку: 1 -> 2 -> 3
        var id1 = new ElementId(1L);
        var id2 = new ElementId(2L);
        var id3 = new ElementId(3L);

        var graph = new ConnectionGraph(id1);
        graph.AddNode(id2);
        graph.AddNode(id3);
        graph.AddEdge(new ConnectionEdge(id1, 0, id2, 0));
        graph.AddEdge(new ConnectionEdge(id2, 1, id3, 0));

        var chain = graph.GetChainFrom(id1).ToList();

        Assert.Equal(3, chain.Count);
        Assert.Contains(chain, e => e.Value == 1);
        Assert.Contains(chain, e => e.Value == 2);
        Assert.Contains(chain, e => e.Value == 3);
    }

    [Fact]
    public void GetChainFrom_MiddleNode_ReturnsAllReachable()
    {
        // Цепочка: 1 -- 2 -- 3 (рёбра двусторонние в GetChainFrom)
        var id1 = new ElementId(1L);
        var id2 = new ElementId(2L);
        var id3 = new ElementId(3L);

        var graph = new ConnectionGraph(id1);
        graph.AddNode(id2);
        graph.AddNode(id3);
        graph.AddEdge(new ConnectionEdge(id1, 0, id2, 0));
        graph.AddEdge(new ConnectionEdge(id2, 1, id3, 0));

        var chain = graph.GetChainFrom(id2).ToList();

        Assert.Equal(3, chain.Count);
    }

    [Fact]
    public void GetChainFrom_Branching_ReturnsAllBranches()
    {
        // Тройник: 1 -> 2, 1 -> 3, 1 -> 4
        var id1 = new ElementId(1L);
        var id2 = new ElementId(2L);
        var id3 = new ElementId(3L);
        var id4 = new ElementId(4L);

        var graph = new ConnectionGraph(id1);
        graph.AddNode(id2);
        graph.AddNode(id3);
        graph.AddNode(id4);
        graph.AddEdge(new ConnectionEdge(id1, 0, id2, 0));
        graph.AddEdge(new ConnectionEdge(id1, 1, id3, 0));
        graph.AddEdge(new ConnectionEdge(id1, 2, id4, 0));

        var chain = graph.GetChainFrom(id1).ToList();

        Assert.Equal(4, chain.Count);
    }
}
