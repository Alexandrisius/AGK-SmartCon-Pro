using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Core.Models;

/// <summary>
/// Immutable graph of connected MEP elements built by BFS traversal.
/// Nodes are elements, edges are connector-to-connector connections.
/// Used for chain depth control in the PipeConnect editor (Phase 8).
/// </summary>
public sealed class ConnectionGraph
{
    private readonly List<ElementId> _nodes;
    private readonly List<ConnectionEdge> _edges;
    private readonly List<List<ElementId>> _levels;
    private readonly Dictionary<long, List<ConnectionRecord>> _originalConnections;
    private readonly Dictionary<long, List<ElementId>> _adjacency;

    internal ConnectionGraph(
        ElementId rootId,
        List<ElementId> nodes,
        List<ConnectionEdge> edges,
        List<List<ElementId>> levels,
        Dictionary<long, List<ConnectionRecord>> originalConnections,
        Dictionary<long, List<ElementId>> adjacency)
    {
        RootId = rootId;
        _nodes = nodes;
        _edges = edges;
        _levels = levels;
        _originalConnections = originalConnections;
        _adjacency = adjacency;
    }

    /// <summary>Root element (dynamic element) from which BFS started.</summary>
    public ElementId RootId { get; }

    /// <summary>All elements in the graph.</summary>
    public IReadOnlyList<ElementId> Nodes => _nodes;

    /// <summary>All connector-to-connector edges.</summary>
    public IReadOnlyList<ConnectionEdge> Edges => _edges;

    /// <summary>Elements grouped by BFS level (level 0 = root).</summary>
    public IReadOnlyList<IReadOnlyList<ElementId>> Levels => _levels;

    /// <summary>Total number of chain elements (excluding root).</summary>
    public int TotalChainElements => Nodes.Count - 1;

    /// <summary>Maximum BFS depth.</summary>
    public int MaxLevel => _levels.Count - 1;

    /// <summary>Original connections of an element captured during BuildGraph (for rollback).</summary>
    public IReadOnlyList<ConnectionRecord> GetOriginalConnections(ElementId elementId)
        => _originalConnections.TryGetValue(elementId.GetValue(), out var list) ? list : [];

    /// <summary>BFS traversal from a given element within the graph.</summary>
    public IEnumerable<ElementId> GetChainFrom(ElementId startId)
    {
        var visited = new HashSet<long> { startId.GetValue() };
        var queue = new Queue<ElementId>();
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;

            if (!_adjacency.TryGetValue(current.GetValue(), out var neighbors)) continue;
            foreach (var neighbor in neighbors)
            {
                if (visited.Add(neighbor.GetValue()))
                    queue.Enqueue(neighbor);
            }
        }
    }
}

/// <summary>
/// Mutable builder for constructing a <see cref="ConnectionGraph"/> during BFS traversal.
/// </summary>
public sealed class ConnectionGraphBuilder
{
    private readonly ElementId _rootId;
    private readonly List<ElementId> _nodes;
    private readonly List<ConnectionEdge> _edges;
    private readonly List<List<ElementId>> _levels;
    private readonly Dictionary<long, List<ConnectionRecord>> _originalConnections = new();

    /// <summary>Creates a builder rooted at the specified element.</summary>
    public ConnectionGraphBuilder(ElementId rootId)
    {
        _rootId = rootId;
        _nodes = [rootId];
        _edges = [];
        _levels = [[rootId]];
    }

    /// <summary>Add a discovered element to the graph.</summary>
    public void AddNode(ElementId elementId)
    {
        _nodes.Add(elementId);
    }

    /// <summary>Add a connection edge between two elements.</summary>
    public void AddEdge(ConnectionEdge edge)
    {
        _edges.Add(edge);
    }

    /// <summary>Register an element at the given BFS level.</summary>
    public void AddElementAtLevel(int level, ElementId elementId)
    {
        while (_levels.Count <= level) _levels.Add([]);
        _levels[level].Add(elementId);
    }

    /// <summary>Save an original connection record for rollback.</summary>
    public void SaveConnection(ElementId elementId, ConnectionRecord record)
    {
        var key = elementId.GetValue();
        if (!_originalConnections.TryGetValue(key, out var list))
        {
            list = [];
            _originalConnections[key] = list;
        }
        if (!list.Any(r => r.ThisConnectorIndex == record.ThisConnectorIndex
                        && r.NeighborElementId.GetValue() == record.NeighborElementId.GetValue()))
            list.Add(record);
    }

    /// <summary>Build the immutable graph from collected data.</summary>
    public ConnectionGraph Build()
    {
        var adjacency = new Dictionary<long, List<ElementId>>();
        foreach (var edge in _edges)
        {
            var fromKey = edge.FromElementId.GetValue();
            var toKey = edge.ToElementId.GetValue();

            if (!adjacency.TryGetValue(fromKey, out var fromList))
            {
                fromList = [];
                adjacency[fromKey] = fromList;
            }
            fromList.Add(edge.ToElementId);

            if (!adjacency.TryGetValue(toKey, out var toList))
            {
                toList = [];
                adjacency[toKey] = toList;
            }
            toList.Add(edge.FromElementId);
        }

        return new ConnectionGraph(_rootId, _nodes, _edges, _levels, _originalConnections, adjacency);
    }
}
