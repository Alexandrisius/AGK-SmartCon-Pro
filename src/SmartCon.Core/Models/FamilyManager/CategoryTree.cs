namespace SmartCon.Core.Models.FamilyManager;

public sealed class CategoryTree
{
    private readonly List<CategoryNode> _nodes;
    private readonly Dictionary<string, CategoryNode> _byId;
    private readonly Dictionary<string, List<CategoryNode>> _children;

    public CategoryTree(IReadOnlyList<CategoryNode> nodes)
    {
        _nodes = new List<CategoryNode>(nodes);
        _byId = new Dictionary<string, CategoryNode>(nodes.Count);
        _children = new Dictionary<string, List<CategoryNode>>(nodes.Count);

        foreach (var node in nodes)
        {
            _byId[node.Id] = node;
            var parentId = node.ParentId ?? string.Empty;
            if (!_children.TryGetValue(parentId, out var list))
            {
                list = new List<CategoryNode>();
                _children[parentId] = list;
            }
            list.Add(node);
        }
    }

    public IReadOnlyList<CategoryNode> GetAllNodes() => _nodes.AsReadOnly();

    public CategoryNode? GetById(string id) => _byId.TryGetValue(id, out var node) ? node : null;

    public IReadOnlyList<CategoryNode> GetChildren(string? parentId)
    {
        var key = parentId ?? string.Empty;
        return _children.TryGetValue(key, out var list) ? list.AsReadOnly() : (IReadOnlyList<CategoryNode>)Array.Empty<CategoryNode>();
    }

    public IReadOnlyList<CategoryNode> GetRootNodes() => GetChildren(null);

    public string GetFullPath(string id)
    {
        var node = GetById(id);
        return node?.FullPath ?? string.Empty;
    }

    public IReadOnlyList<string> GetDescendantIds(string categoryId)
    {
        var result = new List<string>();
        CollectDescendants(categoryId, result);
        return result.AsReadOnly();
    }

    public string BuildFullPath(string id)
    {
        var parts = new Stack<string>();
        var current = GetById(id);
        while (current is not null)
        {
            parts.Push(current.Name);
            current = current.ParentId is not null ? GetById(current.ParentId) : null;
        }
        return string.Join(" > ", parts);
    }

    private void CollectDescendants(string parentId, List<string> result)
    {
        foreach (var child in GetChildren(parentId))
        {
            result.Add(child.Id);
            CollectDescendants(child.Id, result);
        }
    }
}
