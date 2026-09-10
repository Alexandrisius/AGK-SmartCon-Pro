using SmartCon.Core.Models;

namespace SmartCon.Core.Math;

/// <summary>
/// Transition size matching for multi-port families (ADR-053, pure math).
/// When a chain element's port radius mismatches its parent, the element should
/// become a transition fitting (e.g. tee DN25×DN25 → DN20×DN25) instead of a
/// uniform resize — only the port facing the parent changes, all other ports
/// keep their DN and the downstream network is untouched.
///
/// Candidates come from IDynamicSizeResolver.GetAvailableFamilySizes (lookup
/// table rows or family types — #138: only valid combinations allowed).
/// </summary>
public static class TransitionSizeMatcher
{
    /// <summary>
    /// Find the best transition configuration: target port radius must match
    /// exactly (within tolerance), other ports change as little as possible.
    /// </summary>
    /// <param name="candidates">Available family size configurations.</param>
    /// <param name="targetRadius">Desired radius of the parent-facing port (internal units).</param>
    /// <param name="targetConnIdx">Index of the parent-facing connector.</param>
    /// <param name="currentRadii">Current radii of all element connectors, keyed by connector index.</param>
    /// <param name="radiusTolerance">Match tolerance for the target port (internal units).</param>
    /// <returns>Best option, or null when no configuration matches the target radius.</returns>
    public static FamilySizeOption? FindBestTransition(
        IReadOnlyList<FamilySizeOption> candidates,
        double targetRadius,
        int targetConnIdx,
        IReadOnlyDictionary<int, double> currentRadii,
        double radiusTolerance = 1e-5)
    {
        FamilySizeOption? best = null;
        double bestOtherDelta = double.MaxValue;

        foreach (var s in candidates)
        {
            if (s.IsAutoSelect)
                continue;

            // Symbol-changing options are out of scope — only param-driven configurations.
            if (s.SymbolName is not null && s.CurrentSymbolName is not null
                && s.SymbolName != s.CurrentSymbolName)
                continue;

            if (System.Math.Abs(s.Radius - targetRadius) > radiusTolerance)
                continue;

            double otherDelta = OtherPortsDelta(s, targetConnIdx, currentRadii);
            if (otherDelta < bestOtherDelta - Tolerance.Default)
            {
                bestOtherDelta = otherDelta;
                best = s;
            }
        }

        return best;
    }

    /// <summary>
    /// Total radius change on all ports except the target one (internal units).
    /// Zero = clean transition: downstream network keeps every DN.
    /// </summary>
    public static double OtherPortsDelta(
        FamilySizeOption option,
        int targetConnIdx,
        IReadOnlyDictionary<int, double> currentRadii)
    {
        double sum = 0.0;
        foreach (var kvp in option.AllConnectorRadii)
        {
            if (kvp.Key == targetConnIdx)
                continue;
            if (currentRadii.TryGetValue(kvp.Key, out var curR))
                sum += System.Math.Abs(kvp.Value - curR);
        }
        return sum;
    }
}
