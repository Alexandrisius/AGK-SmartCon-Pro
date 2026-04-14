using SmartCon.Core.Models;

namespace SmartCon.Core.Math;

/// <summary>
/// Centralized algorithms for finding the best matching <see cref="FamilySizeOption"/>
/// from a list of candidates based on connector radii proximity.
/// Eliminates duplication across ViewModel size-matching call sites.
/// </summary>
public static class BestSizeMatcher
{
    /// <summary>
    /// Weight applied to the target connector's radius delta.
    /// The target connector (the one being connected) is 3× more important than others.
    /// </summary>
    private const double TargetWeight = 3.0;

    /// <summary>
    /// Find the closest <see cref="FamilySizeOption"/> using weighted multi-connector matching.
    /// Target connector delta is weighted <see cref="TargetWeight"/>× more than other connectors.
    /// </summary>
    /// <param name="candidates">Available size options (non-auto-select).</param>
    /// <param name="targetRadius">Desired radius for the target connector (internal units).</param>
    /// <param name="targetConnIdx">Index of the target connector.</param>
    /// <param name="currentRadii">Current radii of all connectors on the element, keyed by connector index.</param>
    /// <returns>The best matching option, or <c>null</c> if <paramref name="candidates"/> is empty.</returns>
    public static FamilySizeOption? FindClosestWeighted(
        IReadOnlyList<FamilySizeOption> candidates,
        double targetRadius,
        int targetConnIdx,
        IReadOnlyDictionary<int, double> currentRadii)
    {
        if (candidates.Count == 0)
            return null;

        double minTotalDelta = double.MaxValue;
        FamilySizeOption? best = null;

        foreach (var s in candidates)
        {
            double targetDelta = System.Math.Abs(s.Radius - targetRadius);
            double otherDelta = 0;
            foreach (var kvp in s.AllConnectorRadii)
            {
                if (kvp.Key == targetConnIdx) continue;
                if (currentRadii.TryGetValue(kvp.Key, out var curR))
                    otherDelta += System.Math.Abs(kvp.Value - curR);
            }

            double totalDelta = targetDelta * TargetWeight + otherDelta;
            if (totalDelta < minTotalDelta)
            {
                minTotalDelta = totalDelta;
                best = s;
            }
        }

        return best;
    }

    /// <summary>
    /// Find the closest <see cref="FamilySizeOption"/> by primary radius,
    /// with a tiebreaker on other connector deltas when primary deltas are equal.
    /// </summary>
    /// <param name="candidates">Available size options (non-auto-select).</param>
    /// <param name="targetRadius">Desired radius for the target connector (internal units).</param>
    /// <param name="targetConnIdx">Index of the target connector (excluded from tiebreaker).</param>
    /// <param name="currentRadii">Current radii of all connectors, keyed by connector index.</param>
    /// <returns>The best matching option, or <c>null</c> if <paramref name="candidates"/> is empty.</returns>
    public static FamilySizeOption? FindClosestByRadius(
        IReadOnlyList<FamilySizeOption> candidates,
        double targetRadius,
        int targetConnIdx,
        IReadOnlyDictionary<int, double> currentRadii)
    {
        if (candidates.Count == 0)
            return null;

        double minDelta = double.MaxValue;
        FamilySizeOption? best = null;

        foreach (var s in candidates)
        {
            double delta = System.Math.Abs(s.Radius - targetRadius);
            if (delta < minDelta)
            {
                minDelta = delta;
                best = s;
            }
            else if (System.Math.Abs(delta - minDelta) < Tolerance.Default && best is not null)
            {
                double otherDeltaNew = SumOtherDeltas(s, targetConnIdx, currentRadii);
                double otherDeltaBest = SumOtherDeltas(best, targetConnIdx, currentRadii);
                if (otherDeltaNew < otherDeltaBest)
                    best = s;
            }
        }

        return best;
    }

    private static double SumOtherDeltas(
        FamilySizeOption option,
        int excludeConnIdx,
        IReadOnlyDictionary<int, double> currentRadii)
    {
        double sum = 0;
        foreach (var kvp in option.AllConnectorRadii)
        {
            if (kvp.Key == excludeConnIdx) continue;
            if (currentRadii.TryGetValue(kvp.Key, out var curR))
                sum += System.Math.Abs(kvp.Value - curR);
        }
        return sum;
    }
}
