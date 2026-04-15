using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Core.Models;

/// <summary>
/// Directed graph of connected MEP elements.
/// Built before transformation via IElementChainIterator.BuildGraph().
/// Immutable after creation (builder fills via internal methods).
/// </summary>
public sealed class ConnectionGraph
{
    private readonly List<ElementId> _nodes;
    private readonly List<ConnectionEdge> _edges;
    private readonly List<List<ElementId>> _levels = [];
    private readonly Dictionary<long, List<ConnectionRecord>> _originalConnections = new();

    public ConnectionGraph(ElementId rootId)
    {
        RootId = rootId;
        _nodes = [rootId];
        _edges = [];
        _levels.Add([rootId]);
    }

    public ElementId RootId { get; }
    public IReadOnlyList<ElementId> Nodes => _nodes;
    public IReadOnlyList<ConnectionEdge> Edges => _edges;
    public IReadOnlyList<IReadOnlyList<ElementId>> Levels => _levels;
    public int TotalChainElements => Nodes.Count - 1;
    public int MaxLevel => _levels.Count - 1;

    /// <summary>
    /// Add a node to the graph. Called from BuildGraph (BFS).
    /// </summary>
    internal void AddNode(ElementId elementId)
    {
        _nodes.Add(elementId);
    }

    /// <summary>
    /// Add an edge to the graph. Called from BuildGraph (BFS).
    /// </summary>
    internal void AddEdge(ConnectionEdge edge)
    {
        _edges.Add(edge);
    }

    /// <summary>
    /// Add an element at the specified BFS level.
    /// </summary>
    internal void AddElementAtLevel(int level, ElementId elementId)
    {
        while (_levels.Count <= level) _levels.Add([]);
        _levels[level].Add(elementId);
    }

    /// <summary>
    /// Save a record of the original element connection (before disconnect).
    /// </summary>
    internal void SaveConnection(ElementId elementId, ConnectionRecord record)
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

    /// <summary>
    /// Get original connections of an element saved during BuildGraph.
    /// </summary>
    public IReadOnlyList<ConnectionRecord> GetOriginalConnections(ElementId elementId)
        => _originalConnections.TryGetValue(elementId.GetValue(), out var list) ? list : [];

    /// <summary>
    /// All ElementIds reachable from startId (including startId itself).
    /// </summary>
    public IEnumerable<ElementId> GetChainFrom(ElementId startId)
    {
        var visited = new HashSet<ElementId>(ElementIdEqualityComparer.Instance) { startId };
        var queue = new Queue<ElementId>();
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;

            foreach (var edge in _edges)
            {
                ElementId? neighbor = null;

                if (ElementIdEqualityComparer.Instance.Equals(edge.FromElementId, current))
                    neighbor = edge.ToElementId;
                else if (ElementIdEqualityComparer.Instance.Equals(edge.ToElementId, current))
                    neighbor = edge.FromElementId;

                if (neighbor is not null && visited.Add(neighbor))
                {
                    queue.Enqueue(neighbor);
                }
            }
        }
    }
}

/// <summary>
/// EqualityComparer for ElementId — compares by Value (IntegerValue).
/// Revit ElementId does not implement IEquatable correctly for HashSet.
/// </summary>
public sealed class ElementIdEqualityComparer : IEqualityComparer<ElementId>
{
    public static readonly ElementIdEqualityComparer Instance = new();

    public bool Equals(ElementId? x, ElementId? y)
    {
        if (x is null && y is null) return true;
        if (x is null || y is null) return false;
        return x.GetValue() == y.GetValue();
    }

    public int GetHashCode(ElementId obj) => obj.GetStableHashCode();
}
