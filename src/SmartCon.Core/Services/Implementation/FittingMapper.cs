using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// IFittingMapper implementation. Loads mapping from IFittingMappingRepository,
/// returns Priority-ordered rules for a pair of connector types.
/// Shortest path search (Dijkstra) — Phase 9, stub here.
/// </summary>
public sealed class FittingMapper : IFittingMapper
{
    private readonly IFittingMappingRepository _repository;

    public FittingMapper(IFittingMappingRepository repository)
    {
        _repository = repository;
    }

    public IReadOnlyList<FittingMappingRule> GetMappings(
        ConnectionTypeCode from, ConnectionTypeCode to)
    {
        var rules = _repository.GetMappingRules();

        var result = rules
            .Where(r =>
                (r.FromType.Value == from.Value && r.ToType.Value == to.Value) ||
                (r.FromType.Value == to.Value && r.ToType.Value == from.Value
                 && from.Value != to.Value))  // mirror rule, no duplicates when from==to
            .OrderBy(r => r.FittingFamilies.Count == 0 ? int.MaxValue
                         : r.FittingFamilies.Min(f => f.Priority))
            .ToList();

        return result;
    }

    public IReadOnlyList<FittingMappingRule> FindShortestFittingPath(
        ConnectionTypeCode from, ConnectionTypeCode to)
    {
        var direct = GetMappings(from, to);
        if (direct.Count > 0) return direct;

        var rules = _repository.GetMappingRules();
        var allCodes = rules
            .SelectMany(r => new[] { r.FromType.Value, r.ToType.Value })
            .Distinct()
            .ToList();

        var dist = new Dictionary<int, int>();
        var prev = new Dictionary<int, (int code, FittingMappingRule rule)>();
#if NET8_0
        var queue = new System.Collections.Generic.PriorityQueue<int, int>();
#else
        var queue = new Compatibility.SimplePriorityQueue<int, int>();
#endif

        foreach (var c in allCodes) dist[c] = int.MaxValue;
        dist[from.Value] = 0;
        queue.Enqueue(from.Value, 0);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur == to.Value) break;

            foreach (var rule in rules)
            {
                int? neighbor = null;
                if (rule.FromType.Value == cur) neighbor = rule.ToType.Value;
                else if (rule.ToType.Value == cur) neighbor = rule.FromType.Value;

                if (neighbor is null) continue;
                var newDist = dist[cur] + 1;
                if (!dist.TryGetValue(neighbor.Value, out var d) || newDist < d)
                {
                    dist[neighbor.Value] = newDist;
                    prev[neighbor.Value] = (cur, rule);
                    queue.Enqueue(neighbor.Value, newDist);
                }
            }
        }

        if (!dist.TryGetValue(to.Value, out var final) || final == int.MaxValue)
            return [];

        var path = new List<FittingMappingRule>();
        int current = to.Value;
        while (prev.TryGetValue(current, out var entry))
        {
            path.Add(entry.rule);
            current = entry.code;
        }
        path.Reverse();
        return path;
    }

    public void LoadFromFile(string jsonPath)
    {
    }
}
