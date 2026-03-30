using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Направленный граф соединённых MEP-элементов.
/// Строится перед трансформацией через IElementChainIterator.BuildGraph().
/// Неизменяем после создания (builder заполняет через internal-методы).
/// </summary>
public sealed class ConnectionGraph
{
    private readonly List<ElementId> _nodes;
    private readonly List<ConnectionEdge> _edges;

    public ConnectionGraph(ElementId rootId)
    {
        RootId = rootId;
        _nodes = [rootId];
        _edges = [];
    }

    public ElementId RootId { get; }
    public IReadOnlyList<ElementId> Nodes => _nodes;
    public IReadOnlyList<ConnectionEdge> Edges => _edges;

    /// <summary>
    /// Добавить узел в граф. Вызывается из BuildGraph (BFS).
    /// </summary>
    internal void AddNode(ElementId elementId)
    {
        _nodes.Add(elementId);
    }

    /// <summary>
    /// Добавить ребро в граф. Вызывается из BuildGraph (BFS).
    /// </summary>
    internal void AddEdge(ConnectionEdge edge)
    {
        _edges.Add(edge);
    }

    /// <summary>
    /// Все ElementId, достижимые из startId (включая сам startId).
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
/// EqualityComparer для ElementId — сравнивает по Value (IntegerValue).
/// Revit ElementId не реализует IEquatable корректно для HashSet.
/// </summary>
internal sealed class ElementIdEqualityComparer : IEqualityComparer<ElementId>
{
    public static readonly ElementIdEqualityComparer Instance = new();

    public bool Equals(ElementId? x, ElementId? y)
    {
        if (x is null && y is null) return true;
        if (x is null || y is null) return false;
        return x.Value == y.Value;
    }

    public int GetHashCode(ElementId obj) => obj.Value.GetHashCode();
}
