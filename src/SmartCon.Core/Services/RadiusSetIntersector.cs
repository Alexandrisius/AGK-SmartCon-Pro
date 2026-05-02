using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartCon.Core.Services;

public static class RadiusSetIntersector
{
    public static SortedSet<double> Intersect(List<SortedSet<double>> sets, double epsilon = 1e-6)
    {
        if (sets.Count == 0) return [];
        if (sets.Count == 1) return sets[0];

        var result = new SortedSet<double>(sets[0]);
        for (int i = 1; i < sets.Count; i++)
        {
            var keep = new SortedSet<double>();
            foreach (var v in result)
            {
                if (sets[i].Any(s => System.Math.Abs(s - v) < epsilon))
                    keep.Add(v);
            }
            result = keep;
        }

        return result;
    }
}
